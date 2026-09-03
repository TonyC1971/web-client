BSD 2-Clause License

Copyright (c) 2025, andreakarasho
Copyright (c) 2026, rootmancer
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

- Redistributions of source code must retain the above copyright notice, this
  list of conditions and the following disclaimer.

- Redistributions in binary form must reproduce the above copyright notice,
  this list of conditions and the following disclaimer in the documentation
  and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

---

Why this licence, and what it does and does not cover.

The two game clients here are forks of ClassicUO and TazUO, both published by
andreakarasho under this same BSD 2-Clause licence. A derived work is simplest
and safest under the licence it derives from, so that is what this is: the
upstream copyright line is kept, and a second line covers the parts written for
this project — the web layer, the relay, the admin panel and the build tooling.

NOT covered, and not ours to license:

- The Ultima Online game files (.mul / .uop art, maps, sounds, fonts). Those are
  Electronic Arts' and are neither included nor redistributable here. You supply
  your own, from a copy of Ultima Online you already have.
- The vendored upstream libraries under source/*/external (FNA, FAudio, FNA3D,
  SDL2-CS, Theorafile, Myra, FontStashSharp and others). Each keeps its own
  licence file alongside its source; those terms govern, not this file.
- The ModernUO and RunUO/ServUO shard-side handlers under webidentity/, which
  are the reference implementation from ClassicUO/packets and are redistributed
  under their own licence — see webidentity/LICENSE.upstream.md.
