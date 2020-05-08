using System.Net;
using Swihoni.Components;
using Swihoni.Sessions;
using Swihoni.Util.Math;
using Voxel;
using Voxel.Map;

namespace Compound.Session
{
    public class Server : ServerBase, IMiniProvider
    {
        public class Mini : MiniBase
        {
            public override void SetVoxelData(in Position3Int worldPosition, in VoxelChangeData changeData, Chunk chunk = null, bool updateMesh = true)
            {
                base.SetVoxelData(worldPosition, changeData, chunk, updateMesh);
            }
        }

        private readonly Mini m_Mini = new Mini();

        public Server(IPEndPoint ipEndPoint)
            : base(CompoundComponents.SessionElements, ipEndPoint)
        {
        }

        protected override void SettingsTick(Container serverSession)
        {
            base.SettingsTick(serverSession);

            MapManager.Singleton.SetMap(DebugBehavior.Singleton.mapName);
        }

        public override bool IsPaused => ChunkManager.Singleton.ProgressInfo.stage != MapLoadingStage.Completed;

        public void SetVoxelData(in Position3Int worldPosition, in VoxelChangeData changeData, Chunk chunk = null, bool updateMesh = true) { }

        public MiniBase GetMini() => m_Mini;
    }
}