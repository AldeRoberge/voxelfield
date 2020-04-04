using Input;
using Session.Player.Components;
using UnityEngine;

namespace Session.Player.Modifiers
{
    [RequireComponent(typeof(CharacterController))]
    public class PlayerMovement : ModifierBehaviorBase<PlayerComponent>
    {
        private const float DefaultDownSpeed = -1.0f;
        private readonly RaycastHit[] m_CachedGroundHits = new RaycastHit[1];

        [SerializeField] private float
            m_Acceleration = 10.0f,
            m_AirAcceleration = 20.0f,
            m_AirSpeedCap = 2.0f,
            m_MaxSpeed = 6.0f,
            m_MaxAirSpeed = 8.0f,
            m_Friction = 10.0f,
            m_FrictionCutoff = 0.1f,
            m_StopSpeed = 1.0f,
            m_JumpSpeed = 8.5f,
            m_ForwardSpeed = 30.0f,
            m_SideSpeed = 30.0f,
            m_GravityFactor = 23.0f,
            m_SlopeAngleLimit = 45.0f,
            m_MaxStickDistance = 0.25f;

        private CharacterController m_Controller;

        [Header("Movement")] [SerializeField] private LayerMask m_GroundMask = default;
        private byte m_GroundTick;
        private ControllerColliderHit m_Hit;
        private bool m_IsGrounded;

        private float m_SlopeAngle;
        private Vector3 m_Velocity;

        internal override void Setup()
        {
            m_Controller = GetComponent<CharacterController>();
            m_Controller.enabled = false;
        }

        public override void ModifyChecked(PlayerComponent componentToModify, PlayerCommandsComponent commands)
        {
            base.ModifyChecked(componentToModify, commands);
            FullMove(commands);
            componentToModify.position.Value = transform.position;
        }

        public override void ModifyCommands(PlayerCommandsComponent commandsToModify)
        {
            InputProvider input = InputProvider.Singleton;
            commandsToModify.SetInput(PlayerInput.Forward, input.GetInput(InputType.Forward));
            commandsToModify.SetInput(PlayerInput.Backward, input.GetInput(InputType.Backward));
            commandsToModify.SetInput(PlayerInput.Right, input.GetInput(InputType.Right));
            commandsToModify.SetInput(PlayerInput.Left, input.GetInput(InputType.Left));
            commandsToModify.SetInput(PlayerInput.Jump, input.GetInput(InputType.Jump));
        }

        protected override void SynchronizeBehavior(PlayerComponent componentToApply)
        {
            transform.position = componentToApply.position;
            m_Controller.enabled = componentToApply.IsAlive;
        }

        private void FullMove(PlayerCommandsComponent commands)
        {
            Vector3 initialVelocity = m_Velocity;
            float lateralSpeed = LateralMagnitude(m_Velocity);
            m_IsGrounded = m_Controller.isGrounded;
            bool withinAngleLimit = m_SlopeAngle < m_SlopeAngleLimit;
            // TODO make speed slow down based on slope angle
            Transform playerTransform = transform;
            if (withinAngleLimit && m_Velocity.y < 0.0f &&
                Physics.RaycastNonAlloc(playerTransform.position + Vector3.up * 0.05f,
                                        Vector3.down, m_CachedGroundHits, m_MaxStickDistance,
                                        m_GroundMask) > 0)
            {
                float distance = m_CachedGroundHits[0].distance;
                m_IsGrounded = true;
                m_Velocity.y = DefaultDownSpeed;
                if (!commands.GetInput(PlayerInput.Jump)) m_Controller.Move(new Vector3 {y = -distance - 0.06f});
            }
            Vector3 wishDirection =
                commands.GetAxis(PlayerInput.Forward, PlayerInput.Backward) * m_ForwardSpeed * playerTransform.forward +
                commands.GetAxis(PlayerInput.Right, PlayerInput.Left) * m_SideSpeed * playerTransform.right;
            float wishSpeed = wishDirection.magnitude;
            wishDirection.Normalize();
            if (wishSpeed > m_MaxSpeed) wishSpeed = m_MaxSpeed;
            if (m_IsGrounded && withinAngleLimit)
            {
                if (m_GroundTick >= 1)
                {
                    if (lateralSpeed > m_FrictionCutoff) Friction(lateralSpeed, commands.duration);
                    else if (Mathf.Approximately(wishSpeed, 0.0f)) m_Velocity = Vector3.zero;
                    m_Velocity.y = DefaultDownSpeed;
                }
                Accelerate(wishDirection, wishSpeed, m_Acceleration, commands.duration);
                if (commands.GetInput(PlayerInput.Jump))
                {
                    initialVelocity.y = m_JumpSpeed;
                    m_Velocity.y = initialVelocity.y - m_GravityFactor * commands.duration;
                }
                if (m_GroundTick < byte.MaxValue) m_GroundTick++;
            }
            else
            {
                m_GroundTick = 0;
                if (wishSpeed > m_AirSpeedCap) wishSpeed = m_AirSpeedCap;
                Accelerate(wishDirection, wishSpeed, m_AirAcceleration, commands.duration);
                if (withinAngleLimit) m_Velocity.y -= m_GravityFactor * commands.duration;
                float lateralAirSpeed = LateralMagnitude(m_Velocity);
                if (lateralAirSpeed > m_MaxAirSpeed)
                {
                    m_Velocity.x *= m_MaxAirSpeed / lateralAirSpeed;
                    m_Velocity.z *= m_MaxAirSpeed / lateralAirSpeed;
                }
            }
            m_Controller.Move((initialVelocity + m_Velocity) / 2.0f * commands.duration);
        }

        private void OnControllerColliderHit(ControllerColliderHit hit)
        {
            m_Hit = hit;
            m_SlopeAngle = m_IsGrounded ? Vector3.Angle(m_Hit?.normal ?? Vector3.up, Vector3.up) : 0.0f;
        }

        private void Friction(float lateralSpeed, float time)
        {
            float control = lateralSpeed < m_StopSpeed ? m_StopSpeed : lateralSpeed;
            float drop = control * m_Friction * time;
            float newSpeed = lateralSpeed - drop;
            if (newSpeed < 0) newSpeed = 0;
            newSpeed /= lateralSpeed;
            m_Velocity.x *= newSpeed;
            m_Velocity.z *= newSpeed;
        }

        private void Accelerate(Vector3 wishDirection, float wishSpeed, float acceleration, float time)
        {
            float velocityProjection = Vector3.Dot(m_Velocity, wishDirection);
            float addSpeed = wishSpeed - velocityProjection;
            if (addSpeed <= 0.0f) return;
            float accelerationSpeed = acceleration * wishSpeed * time;
            if (accelerationSpeed > addSpeed) accelerationSpeed = addSpeed;
            m_Velocity.x += wishDirection.x * accelerationSpeed;
            m_Velocity.z += wishDirection.z * accelerationSpeed;
        }

        private static float LateralMagnitude(Vector3 v)
        {
            return Mathf.Sqrt(v.x * v.x + v.z * v.z);
        }
    }
}