// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using ClassicUO.Configuration;
using ClassicUO.Game.Managers;
using Microsoft.Xna.Framework;

namespace ClassicUO.Game.GameObjects
{
    public sealed class House : IEquatable<uint>
    {
        private readonly World _world;
        private Dictionary<long, List<Multi>> _spatialIndex = new Dictionary<long, List<Multi>>();
        private bool _spatialIndexDirty = true;

        public House(World world, uint serial, uint revision, bool isCustom)
        {
            _world = world;
            Serial = serial;
            Revision = revision;
            IsCustom = isCustom;
        }

        public uint Serial { get; }
        public List<Multi> Components { get; } = new List<Multi>();

        public Rectangle Bounds;

        public bool Equals(uint other) => Serial == other;

        public bool IsCustom;
        public uint Revision;

        private static long GetSpatialKey(int x, int y) => ((long)x << 32) | (uint)y;

        public void InvalidateSpatialIndex() => _spatialIndexDirty = true;

        private void RebuildSpatialIndex()
        {
            _spatialIndex.Clear();

            for (int i = 0; i < Components.Count; i++)
            {
                Multi m = Components[i];
                if (m.IsDestroyed)
                    continue;

                long key = GetSpatialKey(m.X, m.Y);
                if (!_spatialIndex.TryGetValue(key, out List<Multi> list))
                {
                    list = new List<Multi>();
                    _spatialIndex[key] = list;
                }
                list.Add(m);
            }

            _spatialIndexDirty = false;
        }

        public IReadOnlyList<Multi> GetMultiAt(int x, int y)
        {
            if (_spatialIndexDirty)
                RebuildSpatialIndex();

            long key = GetSpatialKey(x, y);
            if (_spatialIndex.TryGetValue(key, out List<Multi> list))
                return list;

            return Array.Empty<Multi>();
        }

        public Multi Add
        (
            ushort graphic,
            ushort hue,
            ushort x,
            ushort y,
            sbyte z,
            bool iscustom,
            bool ismovable
        )
        {
            var m = Multi.Create(_world, graphic);
            m.Hue = hue;
            m.IsCustom = iscustom;
            m.IsMovable = ismovable;

            m.SetInWorldTile(x, y, z);

            Components.Add(m);

            // Incrementally add to spatial index if it's already built
            if (!_spatialIndexDirty)
            {
                long key = GetSpatialKey(x, y);
                if (!_spatialIndex.TryGetValue(key, out List<Multi> list))
                {
                    list = new List<Multi>();
                    _spatialIndex[key] = list;
                }
                list.Add(m);
            }

            if (ProfileManager.CurrentProfile.ForceHouseTransparency)
            {
                GameObject tile = _world.Map.GetTile(x, y);
                tile.Hue = ProfileManager.CurrentProfile.ForcedTransparencyHouseTileHue;
                Multi.ForcedTransparency = ProfileManager.CurrentProfile.ForcedHouseTransparency;
                m.ForceTransparentHouse = true;
            }

            return m;
        }

        public void ClearCustomHouseComponents(CUSTOM_HOUSE_MULTI_OBJECT_FLAGS state)
        {
            Item item = _world.Items.Get(Serial);

            if (item != null)
            {
                int checkZ = item.Z + 7;

                for (int i = 0; i < Components.Count; i++)
                {
                    Multi component = Components[i];

                    component.State = component.State & ~(CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_TRANSPARENT | CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_IGNORE_IN_RENDER | CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_VALIDATED_PLACE | CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_INCORRECT_PLACE);


                    if (component.IsCustom)
                    {
                        if (component.Z <= item.Z)
                        {
                            if ((component.State & CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_STAIR) == 0)
                            {
                                component.State |= CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_DONT_REMOVE;
                            }
                        }

                        if (state == 0 || (component.State & state) != 0)
                        {
                            component.Destroy();
                        }
                    }
                    else if (component.Z <= checkZ)
                    {
                        component.State = component.State | CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_FLOOR | CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_IGNORE_IN_RENDER;
                    }

                    if (component.IsDestroyed)
                    {
                        Components.RemoveAt(i--);
                        _spatialIndexDirty = true;
                    }
                }
            }
        }

        public void Generate(bool recalculate = false, bool pushtotile = true, bool removePreview = false)
        {
            Item item = _world.Items.Get(Serial);
            //ClearCustomHouseComponents(0);

            if (recalculate)
                _spatialIndexDirty = true;

            foreach (Multi s in Components)
            {
                if (item != null)
                {
                    if (recalculate)
                    {
                        s.SetInWorldTile((ushort)(item.X + s.MultiOffsetX), (ushort)(item.Y + s.MultiOffsetY), (sbyte)(item.Z + s.MultiOffsetZ));
                        s.Offset = Vector3.Zero;
                    }


                    if (removePreview)
                    {
                        s.State &= ~CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_PREVIEW;
                    }

                    s.Hue = item.Hue;
                    //s.State = CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_VALIDATED_PLACE;
                    //s.IsCustom = IsCustom;
                }

                if (!pushtotile)
                {
                    s.RemoveFromTile();
                }
            }

            _world.CustomHouseManager?.GenerateFloorPlace();
        }

        public void ClearComponents(bool removeCustomOnly = false)
        {
            Item item = _world.Items.Get(Serial);

            if (item != null && !item.IsDestroyed)
            {
                item.WantUpdateMulti = true;
            }

            for (int i = 0; i < Components.Count; i++)
            {
                Multi s = Components[i];

                if (!s.IsCustom && removeCustomOnly)
                {
                    continue;
                }

                s.Destroy();
                Components.RemoveAt(i--);
                _spatialIndexDirty = true;
            }

            //Components.Clear();
        }
    }
}
