using System.Net;
using Swihoni.Components;
using Swihoni.Sessions;
using Swihoni.Util.Math;
using Voxel;
using Voxel.Map;

namespace Compound.Session
{
    public class Client : ClientBase
    {
        private class Mini : MiniBase
        {
            public Mini(SessionBase session) : base(session) { }
        }

        private readonly Mini m_Mini;
        private readonly VoxelChangeTransaction m_Transaction = new VoxelChangeTransaction();

        public Client(IPEndPoint ipEndPoint) : base(CompoundComponents.SessionElements, ipEndPoint, Version.String) => m_Mini = new Mini(this);

        protected override void SettingsTick(Container serverSession) => MapManager.Singleton.SetMap(DebugBehavior.Singleton.mapName);

        protected override void Received(Container session)
        {
            base.Received(session);
            var changed = session.Require<ChangedVoxelsProperty>();
            foreach ((Position3Int position, VoxelChangeData change) in changed)
                m_Transaction.AddChange(position, change);
            m_Transaction.Commit();
        }

        public override bool IsPaused => ChunkManager.Singleton.ProgressInfo.stage != MapLoadingStage.Completed;
    }
}