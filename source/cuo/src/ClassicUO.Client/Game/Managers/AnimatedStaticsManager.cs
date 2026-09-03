// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.IO;
using ClassicUO.Assets;
using System;
using System.Collections.Generic;

namespace ClassicUO.Game.Managers
{
    sealed class AnimatedStaticsManager
    {
        // Built once during Initialize from the static-data table and never mutated again.
        // Array (not List<T>) keeps the iteration tight: indexer returns ref via Span and
        // there's no spare-capacity overhead.
        private StaticAnimationInfo[] _staticInfos = Array.Empty<StaticAnimationInfo>();
        private uint _processTime;
        // v0.3.38: round-robin cursor into _staticInfos so Process() can
        // bound its per-frame cost. Pre-fix the loop iterated the entire
        // array every call and the operator's lag-diag log surfaced
        // `[lag-diag] AnimStatics took=13ms` peaks under WASM. With the
        // cursor + a CHUNK budget we cap the per-call work; full-array
        // coverage takes a few hundred ms (one revolution), invisible at
        // 60 fps for tile animations that tick every 100-300 ms anyway.
        private int _processCursor;


        public unsafe void Initialize()
        {
            UOFile file = Client.Game.UO.FileManager.AnimData.AnimDataFile;

            if (file == null)
            {
                return;
            }

            uint lastaddr = (uint)(file.Length - sizeof(AnimDataFrame));

            var infos = new List<StaticAnimationInfo>();
            for (int i = 0; i < Client.Game.UO.FileManager.TileData.StaticData.Length; i++)
            {
                if (Client.Game.UO.FileManager.TileData.StaticData[i].IsAnimated)
                {
                    uint addr = (uint)(i * 68 + 4 * (i / 8 + 1));

                    if (addr <= lastaddr)
                    {
                        infos.Add
                        (
                            new StaticAnimationInfo
                            {
                                Index = (ushort)i,
                                IsField = StaticFilters.IsField((ushort)i)
                            }
                        );
                    }
                }
            }

            _staticInfos = infos.ToArray();
        }

        public unsafe void Process()
        {
            if (_staticInfos.Length == 0)
            {
                return;
            }

            var file = Client.Game.UO.FileManager.AnimData.AnimDataFile;

            if (file == null)
            {
                return;
            }

            // fix static animations time to reflect the standard client
            uint delay = Constants.ITEM_EFFECT_ANIMATION_DELAY * 2;
            bool no_animated_field = ProfileManager.CurrentProfile != null && ProfileManager.CurrentProfile.FieldsType != 0;
            UOFileIndex[] static_data = Client.Game.UO.FileManager.Arts.File.Entries;

            // v0.3.38: chunked round-robin instead of "iterate the entire
            // array every Process()" gated by _processTime. The original
            // gate was cheap when nothing was due, but EXPENSIVE every
            // frame at least one entry was due (it then iterated all
            // ~thousands of entries to compute the next-due minimum).
            // Operator log v0.3.37 surfaced `[lag-diag] AnimStatics took=
            // 13ms` peaks. Chunked round-robin caps the per-call work at
            // CHUNK iterations; full-array coverage in (Length/CHUNK)
            // calls = a few hundred ms even with thousands of entries —
            // invisible to a player since tile animations tick every
            // 100-300 ms anyway. Note: _processTime is NOT consulted now;
            // the bounded chunk runs every Process() call (still cheap).
            const int CHUNK = 256;
            int total = _staticInfos.Length;
            int budget = total < CHUNK ? total : CHUNK;
            for (int k = 0; k < budget; k++)
            {
                int i = _processCursor;
                _processCursor++;
                if (_processCursor >= total)
                {
                    _processCursor = 0;
                }

                ref StaticAnimationInfo o = ref _staticInfos[i];

                if (no_animated_field && o.IsField)
                {
                    o.AnimIndex = 0;

                    continue;
                }

                if (o.Time < Time.Ticks)
                {
                    uint addr = (uint)(o.Index * 68 + 4 * (o.Index / 8 + 1));
                    file.Seek(addr, System.IO.SeekOrigin.Begin);
                    var info = file.Read<AnimDataFrame>();

                    byte offset = o.AnimIndex;

                    if (info.FrameInterval > 0)
                    {
                        o.Time = Time.Ticks + info.FrameInterval * delay + 1;
                    }
                    else
                    {
                        o.Time = Time.Ticks + delay;
                    }

                    if (offset < info.FrameCount && o.Index + 0x4000 < static_data.Length)
                    {
                        static_data[o.Index + 0x4000].AnimOffset = info.FrameData[offset++];
                    }

                    if (offset >= info.FrameCount)
                    {
                        offset = 0;
                    }

                    o.AnimIndex = offset;
                }
            }
        }


        private struct StaticAnimationInfo
        {
            public uint Time;
            public ushort Index;
            public byte AnimIndex;
            public bool IsField;
        }
    }
}