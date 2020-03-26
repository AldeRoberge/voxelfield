using Collections;

namespace Session
{
    public class SessionStates : CyclicArray<SessionState>
    {
        public SessionStates(int size) : base(size, () => new SessionState())
        {
        }
    }
}