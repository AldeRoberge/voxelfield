using System;
using LiteNetLib.Utils;
using Swihoni.Components;
using UnityEngine;

namespace Voxels.Map
{
    [Serializable]
    public class VersionProperty : StringProperty
    {
    }

    [Serializable]
    public class MapContainer : Container
    {
        public StringProperty name;
        public VersionProperty version;
        public IntProperty terrainHeight;
        public DimensionComponent dimension;
        public TerrainGenerationComponent terrainGeneration;

        public OrderedVoxelChangesProperty voxelChanges;
        public ModelsProperty models;
        public BoolProperty breakableEdges;

        public MapContainer Deserialize(NetDataReader reader)
        {
            foreach (ElementBase element in Elements)
            {
                element.Deserialize(reader);
                // ReSharper disable once PossibleNullReferenceException
                if (ReferenceEquals(element, version)) voxelChanges.Version = version.AsNewString();
            }
            Debug.Log($"[{GetType().Name}] Read map has: {voxelChanges.Count} voxel changes");
            if (version != Application.version) Debug.Log($"[{GetType().Name}] Converting map container to newest version, from {version} to {Application.version}");
            return this;
        }
    }
}