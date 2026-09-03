// Simplified wasm variant of IsometricWorld.fx.
//
// 2026-04-22 revision: add the four extra world-render modes that
// were falling through to `color * alpha` unchanged. That fallthrough
// caused three user-visible bugs:
//   * no shadows (SHADOW mode rendered the shadow sprite in its
//     native colour instead of the flat black silhouette the mesh
//     is supposed to be — still in the skew geometry, just the wrong
//     colour).
//   * dead character B&W broken (land tiles use SHADER_LAND_HUED
//     when the local player is dead; the shader didn't handle mode 6
//     so land rendered in full colour while statics/mobiles correctly
//     desaturated through SHADER_HUED).
//   * spell effects wrongly tinted (EFFECT_HUED falls through for
//     skill effects like poison clouds).
//
// The added modes: LAND_COLOR (6), SPECTRAL (7), SHADOW (8),
// EFFECT_HUED (10). LAND (5) and LIGHTS (9) stay fallthrough —
// LAND only changes lighting (the `get_light` path uses the vertex
// Normal which this shader doesn't pipe through, and Brightlight is
// a null param on wasm so lighting would be a no-op); LIGHTS is
// unreachable because GameScene.PrepareLightsRendering short-
// circuits the light render pass under BROWSER_WASM.
//
// Kept from the previous revision: four HUED modes with/without
// GUMP offset, manual remainder (no `%`), `floor()` instead of
// `int()`, ps_2_0 target.

#define HUED              1
#define PARTIAL_HUED      2
#define HUE_TEXT_NO_BLACK 3
#define HUE_TEXT          4
#define LAND              5
#define LAND_COLOR        6
#define SPECTRAL          7
#define SHADOW            8
#define LIGHTS            9
#define EFFECT_HUED       10
#define GUMP              20

const static float3 LIGHT_DIRECTION = float3(0.0f, 1.0f, 1.0f);
const static float HUE_ROWS           = 1024;
const static float HUE_COLUMNS        = 16;
const static float HUE_WIDTH          = 32;
const static float HUES_PER_TEXTURE   = HUE_ROWS * HUE_COLUMNS;

float4x4 MatrixTransform;
float4x4 WorldMatrix;
float2   Viewport;
// User report 2026-04-22: ground tiles look blocky/patchy on wasm
// vs smooth shaded gradients on desktop. Root cause: the LAND mode
// was a fall-through, so land rendered at its raw art brightness
// without the per-normal lighting factor desktop applies. Declare
// Brightlight so BasicUOEffect's Parameters["Brightlight"] returns
// non-null and `batcher.SetBrightlight(TerrainShadowsLevel * 0.1f)`
// in GameScene flows through. Default profile TerrainShadowsLevel
// is 15 -> Brightlight = 1.5.
float Brightlight;
// Circle of Transparency (user report 2026-04-22: "se ve todo
// brillante" when CoT active). C# encodes the useTrans sentinel as
// `alpha + 1.0` on the Hue.Z component (see desktop IsometricWorld.
// fx:107-110 for the matching decode). The old wasm shader didn't
// decode it, so alpha read ~2.0 and `color * alpha` saturated to
// white. Declaring this uniform lets BasicUOEffect pick it up and
// `batcher.SetCircleOfTransparencyRadius(...)` in GameScene flows
// through. Default is 0 (feature off) until the user opts in.
float CircleOfTransparencyRadius;

// Global scene light multiplier [0..1] for day/night cycle.
// Bug O4 partial: the full LIGHTS render pass stays gated on
// the RT composite (PrepareLightsRendering early-returns under
// BROWSER_WASM). A global darkness factor does NOT need the
// custom FBO — it's a uniform multiplier on the final colour
// driven by `World.Light.IsometricLevel`. Default 1.0 (bright,
// no change) until GameScene.Draw sets it each frame.
// Torch/campfire glow still needs the per-light additive pass,
// deferred until the RT infrastructure lands.
float GlobalLight = 1.0f;

// Sprite smoothing (operator 2026-07-10): texel-AA / sharp-bilinear via 4
// manual point taps at texel centres. SmoothMode 0=off 1=silhouette 2=full;
// SmoothWidth 0..1 slider (fwidth-scaled band); DrawTexSize set by Batcher2D
// per texture switch. Gated to world sprites (gumps/text/LIGHTS excluded).
float  SmoothWidth = 0.0f;
float  SmoothMode  = 0.0f;
float2 DrawTexSize = float2(0.0f, 0.0f);

sampler DrawSampler : register(s0);
sampler HueSampler0 : register(s1);
// LightColors LUT bound from Client.cs (Textures[2] = hueSamplers[1]).
// Used by the LIGHTS mode to colorize the white halo gradient with
// the light's per-graphic colour-table entry. Without it, mode==LIGHTS
// (coloured non-hued lights — most magic / spell glows, some lanterns)
// fell through to the raw light-texture grayscale. Matches desktop
// IsometricWorld.fx:29.
sampler HueSampler1 : register(s2);

struct VS_INPUT
{
    float4 Position : POSITION0;
    float3 Normal   : NORMAL0;
    float3 TexCoord : TEXCOORD0;
    float3 Hue      : TEXCOORD1;
};

struct PS_INPUT
{
    float4 Position : POSITION0;
    float3 TexCoord : TEXCOORD0;
    float3 Normal   : TEXCOORD2;
    float3 Hue      : TEXCOORD1;
    float3 PixelPos : TEXCOORD3;
};

PS_INPUT VertexShaderFunction(VS_INPUT IN)
{
    PS_INPUT OUT;
    OUT.Position = mul(mul(IN.Position, WorldMatrix), MatrixTransform);
    OUT.Position.x -= 0.5 / Viewport.x;
    OUT.Position.y += 0.5 / Viewport.y;
    OUT.TexCoord = IN.TexCoord;
    OUT.Normal   = IN.Normal;
    OUT.Hue      = IN.Hue;
    // PixelPos carries the clip-space XY for the CoT disc
    // calculation in the pixel shader. Desktop does this the same
    // way — see IsometricWorld.fx:89.
    OUT.PixelPos = OUT.Position.xyz;
    return OUT;
}

// Per-vertex light factor used by LAND / LAND_COLOR. Interpolates
// across the tile via the Normal varying, producing the smooth
// shading between adjacent tiles that desktop ships. The constant
// 0.85355 is cos(45°)/2 + 0.5 — the lit factor of a flat tile at
// UO's default 45° isometric light angle; Brightlight modulates
// the deviation from flat.
float get_light(float3 norm)
{
    float3 light = normalize(LIGHT_DIRECTION);
    float3 normal = normalize(norm);
    float base = (max(dot(normal, light), 0.0f) / 2.0f) + 0.5f;
    return base + ((Brightlight * (base - 0.85355339f)) - (base - 0.85355339f));
}

// Look up hue index `hue` in HueSampler0 at brightness `gray`.
// The LUT is a 512x1024 texture organised as 16 columns of
// 32-pixel-wide hue rows x 1024 rows = 16384 hues. See
// Client.cs:LoadUOFiles for the build-side layout.
float3 get_rgb(float gray, float hue)
{
    float halfPixelX     = (1.0f / (HUE_COLUMNS * HUE_WIDTH)) * 0.5f;
    float hueColumnWidth = 1.0f / HUE_COLUMNS;
    float hueStart       = frac(hue / HUE_COLUMNS);

    float xPos = hueStart + gray / HUE_COLUMNS;
    xPos = clamp(xPos, hueStart + halfPixelX, hueStart + hueColumnWidth - halfPixelX);

    // `hue % HUES_PER_TEXTURE` rewritten as manual remainder to dodge
    // the MojoShader glsles3 `%` transpile trap.
    float yMod = hue - floor(hue / HUES_PER_TEXTURE) * HUES_PER_TEXTURE;
    float yPos = yMod / (HUES_PER_TEXTURE - 1);

    return tex2D(HueSampler0, float2(xPos, yPos)).rgb;
}

// Sample the LightColors LUT (HueSampler1) for mode==LIGHTS.
// `shader` is IN.Hue.x - 1 (matches desktop IsometricWorld.fx:71).
// `gray` is the light texture's red channel (the gradient intensity).
float3 get_colored_light(float shader, float gray)
{
    float2 texcoord = float2(gray, (shader - 0.5) / 63);
    return tex2D(HueSampler1, texcoord).rgb;
}

// Resolve one texel tap's final RGB for the smoothable world modes.
// Mirrors the mode branches in PixelShader_Hue 1:1 — the FULL smoothing
// quality blends colours AFTER the hue LUT resolves (blending the raw
// sample would interpolate the palette INDEX and smear hue edges).
float3 resolve_rgb(float4 c, float mode, float hue, float3 normal)
{
    if (mode == HUED)
    {
        return get_rgb(c.r, hue);
    }
    if (mode == PARTIAL_HUED)
    {
        if (c.r == c.g && c.r == c.b)
        {
            return get_rgb(c.r, hue);
        }
        return c.rgb;
    }
    if (mode == EFFECT_HUED)
    {
        return get_rgb(c.g, hue);
    }
    if (mode == LAND_COLOR)
    {
        return get_rgb(c.r, hue) * get_light(normal);
    }
    if (mode == LAND)
    {
        return c.rgb * get_light(normal);
    }
    return c.rgb; // NONE
}

float4 PixelShader_Hue(PS_INPUT IN) : COLOR0
{
    float4 color = tex2D(DrawSampler, IN.TexCoord.xy);

    // Derivatives MUST be computed before any discard/branch (GLSL: undefined
    // in non-uniform control flow) — hoisted for the smoothing block below.
    float2 smoothWpx = max(fwidth(IN.TexCoord.xy * DrawTexSize), 1e-4f);

    if (color.a == 0.0f)
        discard;

    // Decode the CoT useTrans sentinel: C# sets alpha += 1.0f for
    // sprites that should fade inside the transparency disc (see
    // `ShaderHueTranslator.GetHueVector`'s `circletrans` path +
    // desktop IsometricWorld.fx:105-110 for the original decode).
    // The old wasm shader skipped this decode, so alpha read ~2.0
    // and `color * alpha` saturated to white ("se ve todo
    // brillante" — user report 2026-04-22).
    float alpha = IN.Hue.z;
    bool useTrans = false;
    if (alpha > 1.0f)
    {
        useTrans = true;
        alpha -= 1.0f;
    }
    if (alpha == 0.0f)
        discard;

    // mode as float + step comparisons — keeps the ps_2_0 transpile
    // happy and avoids int() casts.
    float mode = floor(IN.Hue.y);
    float hue  = IN.Hue.x;
    // Track whether the sprite is a gump so we can skip world-
    // render-only effects (like the day/night GlobalLight) at the
    // bottom of the shader.
    bool isGump = false;

    if (mode >= GUMP)
    {
        mode -= GUMP;
        isGump = true;
        // Gump black-pixel protection: very dark pixels keep their
        // original colour so sprite silhouettes don't recolour into
        // the hue band. Matches IsometricWorld.fx behaviour.
        if (color.r < 0.02f)
        {
            hue = 0;
        }
    }

    // Perf rewrite 2026-04-22: the previous version called
    // `get_rgb` (= `tex2D(HueSampler0, ...)` + ~12 ALU ops) BEFORE
    // the mode dispatch, so every pixel paid the LUT fetch even
    // when mode was LAND / NONE (the vast majority of world
    // pixels). Land-only scenes tanked from 60 → ~45 fps at 1080p.
    // Now: deal with the modes that don't need the LUT first
    // (SHADOW, SPECTRAL) via early-out + keep each hue-using mode's
    // `get_rgb` call inside its own branch so it only runs when
    // actually needed. X4121 "gradient in flow control" fires for
    // each conditional tex2D — acceptable cost (derivative is
    // cheap, the gate is on the expensive sample).

    if (mode == SHADOW)
    {
        // Flat black silhouette at 40 % opacity. Batcher2D.DrawShadow
        // already skews the sprite geometry and sets Hue.Z=1 so the
        // early alpha==0 discard doesn't swallow the fragment.
        return float4(0.0f, 0.0f, 0.0f, 0.4f);
    }
    if (mode == SPECTRAL)
    {
        // Ghost rendering: alpha ramps with source R, colour black.
        float specAlpha = 1.0f - (color.r * 1.5f);
        return float4(0.0f, 0.0f, 0.0f, specAlpha);
    }

    // ── Sprite smoothing (texel-AA) ────────────────────────────────
    // World sprites only: gumps, text modes and LIGHTS keep the crisp
    // legacy render (SHADOW/SPECTRAL already early-returned above).
    // Four point taps at the surrounding texel centres, blended with
    // fwidth-scaled smoothstep weights so the transition band hugs
    // ~1 screen pixel at any zoom/DPR. The TextureAtlas packs a 1px
    // transparent border per sprite, so edge taps read clean alpha=0
    // instead of a neighbouring sprite.
    bool smoothed = false;
    if (SmoothMode > 0.5f && SmoothWidth > 0.001f && !isGump
        && DrawTexSize.x > 1.0f
        && mode != HUE_TEXT && mode != HUE_TEXT_NO_BLACK && mode != LIGHTS)
    {
        float2 ts  = DrawTexSize;
        float2 tuv = IN.TexCoord.xy * ts - 0.5f;
        float2 tc  = floor(tuv);
        float2 f   = tuv - tc;
        float2 k   = min(smoothWpx * (0.25f + 0.75f * SmoothWidth), 0.5f);
        // Clamped LINEAR ramp (v0.9.371, plan §piggyback): the exact box-
        // filter integral of a texel edge over the pixel footprint. The
        // previous smoothstep's cubic falloff slightly over-darkened the
        // band ends (pseudo-bandlimited argument — Maister); a straight
        // ramp is the correct antiderivative and reads a touch crisper.
        float2 g   = saturate((f - (0.5f - k)) / (2.0f * k));
        float2 uv00 = (tc + 0.5f) / ts;
        float2 o    = 1.0f / ts;
        float4 c00 = tex2D(DrawSampler, uv00);
        float4 c10 = tex2D(DrawSampler, uv00 + float2(o.x, 0.0f));
        float4 c01 = tex2D(DrawSampler, uv00 + float2(0.0f, o.y));
        float4 c11 = tex2D(DrawSampler, uv00 + o);
        float w00 = (1.0f - g.x) * (1.0f - g.y);
        float w10 = g.x * (1.0f - g.y);
        float w01 = (1.0f - g.x) * g.y;
        float w11 = g.x * g.y;
        float sa = c00.a * w00 + c10.a * w10 + c01.a * w01 + c11.a * w11;

        if (SmoothMode > 1.5f)
        {
            // FULL: resolve each tap through the hue pipeline, then an
            // alpha-weighted blend so transparent taps never pull the
            // colour toward black at silhouettes.
            float aw00 = w00 * c00.a;
            float aw10 = w10 * c10.a;
            float aw01 = w01 * c01.a;
            float aw11 = w11 * c11.a;
            float awSum = max(aw00 + aw10 + aw01 + aw11, 1e-4f);
            float3 rgb = resolve_rgb(c00, mode, hue, IN.Normal) * aw00
                       + resolve_rgb(c10, mode, hue, IN.Normal) * aw10
                       + resolve_rgb(c01, mode, hue, IN.Normal) * aw01
                       + resolve_rgb(c11, mode, hue, IN.Normal) * aw11;
            color.rgb = rgb / awSum;
            smoothed = true; // hue already resolved — skip the branches below
        }
        // SILHOUETTE (and FULL): fold the smoothed coverage into the same
        // whole-sprite `alpha` scalar the CoT fade uses — the `color * alpha`
        // return then behaves identically to a proven-in-prod alpha fade,
        // regardless of the batcher's blend convention.
        alpha *= sa;
        if (alpha <= 0.004f)
            discard;
    }

    if (smoothed)
    {
        // colour resolved per-tap above
    }
    else if (mode == HUED)
    {
        color.rgb = get_rgb(color.r, hue);
    }
    else if (mode == PARTIAL_HUED)
    {
        // Only recolour neutral grey pixels (r==g==b).
        if (color.r == color.g && color.r == color.b)
        {
            color.rgb = get_rgb(color.r, hue);
        }
    }
    else if (mode == HUE_TEXT_NO_BLACK)
    {
        if (color.r > 0.04f || color.g > 0.04f || color.b > 0.04f)
        {
            color.rgb = get_rgb(1.0f, hue);
        }
    }
    else if (mode == HUE_TEXT)
    {
        // v0.8.4 FIX: tint the FontStashSharp glyph (white, alpha-masked) by the
        // RGBA colour that IFontStashRenderer.Draw packs into IN.Normal (r,g,b) —
        // matching the DESKTOP IsometricWorld.fx, where this branch is
        // `color.rgb = color.rgb * IN.Normal;` and the get_rgb(1.0,hue) line is
        // COMMENTED OUT. The wasm port had copied the commented-out wrong version,
        // so ALL TTF text (journal / overhead speak / side-chat / nameplates, which
        // TazUO renders via avadonian.ttf) came out as get_rgb(1.0, hue=0) = DARK,
        // discarding its real colour. mode==HUE_TEXT(4) is used ONLY by the TTF
        // glyph path, so this is safe; CUO (classic hued font path) is unaffected.
        color.rgb = color.rgb * IN.Normal;
    }
    else if (mode == LAND_COLOR)
    {
        // Stretched land tiles with a server-assigned hue. Desktop
        // multiplies the hued colour by `get_light(Normal)` — now
        // that we pipe Normal + Brightlight through, we match.
        color.rgb = get_rgb(color.r, hue) * get_light(IN.Normal);
    }
    else if (mode == EFFECT_HUED)
    {
        // Spell / skill effects hue by their green channel.
        color.rgb = get_rgb(color.g, hue);
    }
    else if (mode == LAND)
    {
        // Unhued stretched land tiles. `get_light` returns a per-
        // vertex interpolated factor ~0.85 for flat tiles, lighter
        // for tiles facing the light, darker for tiles facing away.
        // The GPU-interpolated Normal gives the smooth gradient
        // between tiles desktop shows — without this modulation,
        // each tile rendered at full raw brightness and the
        // transitions between heightmap steps looked blocky
        // (user report 2026-04-22 "algunos tiles del suelo se ven
        // más oscuros").
        color.rgb *= get_light(IN.Normal);
    }
    else if (mode == LIGHTS)
    {
        // Coloured-non-hued lights. PrepareLightsRendering sets this
        // when l.Color > 1.0 && !l.IsHue (most magic / spell glows
        // and some decorative lamps). The HueSampler1 LUT colorizes
        // the white halo gradient by the light's per-graphic palette
        // entry. Matches desktop IsometricWorld.fx:165-168.
        color.rgb = get_colored_light(IN.Hue.x - 1, color.r);
    }
    // NONE (0) intentionally falls through — the light texture is
    // already a white-faded gradient and we want the multiplicative
    // composite in RenderTargets.Draw to use it as-is.

    // Circle of Transparency disc. For sprites flagged useTrans,
    // fade alpha toward zero inside an 85-100 % radius shell
    // centred on the player. Matches desktop IsometricWorld.fx:
    // 174-190 exactly.
    if (useTrans && CircleOfTransparencyRadius > 0)
    {
        float2 pixelDist = IN.PixelPos.xy * Viewport * 0.5;
        float ratio = length(pixelDist) / CircleOfTransparencyRadius;

        if (ratio < 0.85f)
            discard;

        if (ratio < 1.0f)
        {
            float t = (ratio - 0.85f) / 0.15f;
            alpha *= t * t * t;

            if (alpha < 0.02f)
                discard;
        }
    }

    // Bug O4 partial: apply the day/night darkness AFTER mode-
    // specific hue work so hued sprites darken alongside untinted
    // terrain. Floors at 0.08 so even pitch-black nights leave the
    // scene readable. GATED on !isGump — the same shader runs for
    // gumps (paperdoll, login screen, HUD, char-select), and those
    // must NOT darken with the world's day/night cycle. User
    // report 2026-04-24: "solo debería aplicar dentro de la game
    // view y no dentro de todo el cliente".
    if (!isGump)
    {
        color.rgb *= max(GlobalLight, 0.08f);
    }

    return color * alpha;
}

technique HueTechnique
{
    pass p0
    {
        VertexShader = compile vs_3_0 VertexShaderFunction();
        PixelShader  = compile ps_3_0 PixelShader_Hue();
    }
}
