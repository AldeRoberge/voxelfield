using System;
using Swihoni.Sessions.Player.Components;
using UnityEngine;

namespace Swihoni.Sessions.Items.Modifiers
{
    public static class ItemStatusId
    {
        public const byte Idle = 0,
                          PrimaryUsing = 1,
                          SecondaryUsing = 2,
                          Last = 2;
    }

    public static class ItemEquipStatusId
    {
        public const byte Unequipping = 0,
                          Equipping = 1,
                          Equipped = 2,
                          Unequipped = 3;
    }

    public static class ItemId
    {
        public const byte None = 0,
                          TestingRifle = 1,
                          Grenade = 2,
                          Molotov = 3,
                          Shotgun = 4,
                          C4 = 5,
                          Shovel = 6,
                          Last = 6;
    }

    [Serializable]
    public class ItemStatusModiferProperties
    {
        public float duration;
        public bool isPersistent;
    }

    [CreateAssetMenu(fileName = "Item", menuName = "Item/Item", order = 0)]
    public class ItemModifierBase : ScriptableObject
    {
        public byte id;
        public string itemName;
        public float movementFactor = 1.0f;
        [SerializeField] protected ItemStatusModiferProperties[] m_StatusModiferProperties, m_EquipStatusModiferProperties;
        public ItemStatusModiferProperties GetStatusModifierProperties(byte statusId) => m_StatusModiferProperties[statusId];

        public ItemStatusModiferProperties GetEquipStatusModifierProperties(byte equipStatusId) => m_EquipStatusModiferProperties[equipStatusId];

        public virtual void ModifyTrusted(SessionBase session, int playerId, ItemComponent item, InventoryComponent inventory, InputFlagProperty inputProperty, float duration) { }

        public virtual void ModifyChecked(SessionBase session, int playerId, ItemComponent item, InventoryComponent inventory, InputFlagProperty inputProperty, float duration)
        {
            if (CanUse(item, inventory))
            {
                if (inputProperty.GetInput(PlayerInput.UseOne))
                    StartStatus(session, playerId, item, GetUseStatus(inputProperty), duration);
                else if (HasSecondaryUse() && inputProperty.GetInput(PlayerInput.UseTwo))
                    StartStatus(session, playerId, item, ItemStatusId.SecondaryUsing, duration);
            }
            ModifyStatus(session, playerId, item, inventory, inputProperty, duration);
        }

        private void ModifyStatus(SessionBase session, int playerId, ItemComponent item, InventoryComponent inventory, InputFlagProperty inputs, float duration)
        {
            item.status.elapsed.Value += duration;
            StatusTick(session, playerId, item, inputs, duration);
            EndStatus(session, playerId, item, inventory, inputs, duration);
        }

        protected virtual void EndStatus(SessionBase session, int playerId, ItemComponent item, InventoryComponent inventory, InputFlagProperty inputs, float duration)
        {
            ByteStatusComponent status = item.status;
            ItemStatusModiferProperties modifierProperties;
            while (status.elapsed > (modifierProperties = m_StatusModiferProperties[status.id]).duration)
            {
                if (modifierProperties.isPersistent) break;
                float statusElapsed = status.elapsed;
                byte? nextStatus = FinishStatus(session, playerId, item, inventory, inputs);
                StartStatus(session, playerId, item, nextStatus ?? ItemStatusId.Idle, duration, statusElapsed - modifierProperties.duration);
            }
        }

        protected virtual void StatusTick(SessionBase session, int playerId, ItemComponent item, InputFlagProperty inputs, float duration) { }

        protected void StartStatus(SessionBase session, int playerId, ItemComponent itemComponent, byte statusId, float duration, float elapsed = 0.0f)
        {
            itemComponent.status.id.Value = statusId;
            itemComponent.status.elapsed.Value = elapsed;
            switch (statusId)
            {
                case ItemStatusId.PrimaryUsing:
                    PrimaryUse(session, playerId, itemComponent, duration);
                    break;
                case ItemStatusId.SecondaryUsing:
                    SecondaryUse(session, playerId, duration);
                    break;
            }
        }

        /// <returns>State to switch to</returns>
        protected virtual byte? FinishStatus(SessionBase session, int playerId, ItemComponent item, InventoryComponent inventory, InputFlagProperty inputs)
        {
            switch (item.status.id)
            {
                case ItemStatusId.SecondaryUsing:
                    return ItemStatusId.Idle;
                case ItemStatusId.PrimaryUsing:
                    if (CanUse(item, inventory, true) && inputs.GetInput(PlayerInput.UseOne))
                        return GetUseStatus(inputs);
                    break;
            }
            return null;
        }

        protected virtual byte GetUseStatus(InputFlagProperty inputProperty) => ItemStatusId.PrimaryUsing;

        protected virtual bool CanUse(ItemComponent item, InventoryComponent inventory, bool justFinishedUse = false) =>
            (item.status.id != ItemStatusId.PrimaryUsing || justFinishedUse)
         && item.status.id != ItemStatusId.SecondaryUsing
         && inventory.equipStatus.id == ItemEquipStatusId.Equipped;

        protected virtual void SecondaryUse(SessionBase session, int playerId, float duration) { }

        protected virtual void PrimaryUse(SessionBase session, int playerId, ItemComponent item, float duration) { }

        public void ModifyCommands(SessionBase session, InputFlagProperty commandsToModify) { }

        protected virtual bool HasSecondaryUse() => false;

        internal virtual void OnUnequip(SessionBase session, int playerId, ItemComponent itemComponent, float duration) { }
    }
}