using Input;
using Swihoni.Components;
using Swihoni.Sessions.Player.Components;
using UnityEngine;

namespace Swihoni.Sessions.Player.Modifiers
{
    public class PlayerCameraBehavior : PlayerModifierBehaviorBase
    {
        [SerializeField] private float m_Sensitivity = 10.0f;

        public override void ModifyTrusted(Container containerToModify, Container commandsContainer, float duration)
        {
            if (containerToModify.Without(out CameraComponent cameraComponent)
             || commandsContainer.Without(out MouseComponent mouseComponent)
             || containerToModify.Has<ServerComponent>() // TODO:refactor use whether or not mouse component has values?
             || containerToModify.Present(out HealthProperty healthProperty) && healthProperty.IsDead) return;
            base.ModifyTrusted(containerToModify, commandsContainer, duration);
            cameraComponent.yaw.Value = Mathf.Repeat(cameraComponent.yaw + mouseComponent.mouseDeltaX * m_Sensitivity, 360.0f);
            cameraComponent.pitch.Value = Mathf.Clamp(cameraComponent.pitch - mouseComponent.mouseDeltaY * m_Sensitivity, -90.0f, 90.0f);
        }

        public override void ModifyCommands(Container commandsToModify)
        {
            if (commandsToModify.Without(out MouseComponent mouseComponent)) return;
            mouseComponent.mouseDeltaX.Value = InputProvider.GetMouseInput(MouseMovement.X);
            mouseComponent.mouseDeltaY.Value = InputProvider.GetMouseInput(MouseMovement.Y);
        }

        protected override void SynchronizeBehavior(Container containersToApply)
        {
            if (containersToApply.Without(out CameraComponent cameraComponent)) return;
            if (cameraComponent.yaw.HasValue) transform.rotation = Quaternion.AngleAxis(cameraComponent.yaw, Vector3.up);
        }
    }
}