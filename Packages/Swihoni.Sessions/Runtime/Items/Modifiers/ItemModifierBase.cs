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
                          Last = SecondaryUsing;
    }

    public static class ItemEquipStatusId
    {
        public const byte Unequipping = 0, Equipping = 1, Equipped = 2, Unequipped = 3;
    }

    public static class ItemId
    {
        public const byte None = 0,
                          TestingRifle = 1,
                          Last = TestingRifle;
    }

    [Serializable]
    public class ItemStatusModiferProperties
    {
        public float duration;
    }

    [CreateAssetMenu(fileName = "Item", menuName = "Item/Item", order = 0)]
    public class ItemModifierBase : ScriptableObject
    {
        public byte id;
        public string itemName;
        public float movementFactor = 1.0f;
        [SerializeField] private ItemStatusModiferProperties[] m_StatusModiferProperties = default,
                                                               m_EquipStatusModiferProperties = default;
        public ItemStatusModiferProperties GetStatusModifierProperties(byte statusId) => m_StatusModiferProperties[statusId];

        public ItemStatusModiferProperties GetEquipStatusModifierProperties(byte equipStatusId) => m_EquipStatusModiferProperties[equipStatusId];

        public virtual void ModifyTrusted(SessionBase session, int playerId, ItemComponent item, InventoryComponent inventory, InputFlagProperty inputProperty, float duration) { }

        public virtual void ModifyChecked(SessionBase session, int playerId, ItemComponent item, InventoryComponent inventory, InputFlagProperty inputProperty, float duration)
        {
            if (CanUse(item, inventory))
            {
                if (inputProperty.GetInput(PlayerInput.UseOne))
                    StartStatus(session, playerId, item, GetUseStatus(inputProperty));
                else if (HasSecondaryUse() && inputProperty.GetInput(PlayerInput.UseTwo))
                    StartStatus(session, playerId, item, ItemStatusId.SecondaryUsing);
            }
            ModifyStatus(session, playerId, item, inventory, inputProperty, duration);
        }

        private void ModifyStatus(SessionBase session, int playerId, ItemComponent item, InventoryComponent inventory, InputFlagProperty inputProperty, float duration)
        {
            ByteStatusComponent status = item.status;
            status.elapsed.Value += duration;
            ItemStatusModiferProperties modifierProperties;
            while (status.elapsed > (modifierProperties = m_StatusModiferProperties[status.id]).duration)
            {
                float statusElapsed = status.elapsed;
                byte? nextStatus = FinishStatus(item, inventory, inputProperty);
                StartStatus(session, playerId, item, nextStatus ?? ItemStatusId.Idle, statusElapsed - modifierProperties.duration);
            }
        }

        protected void StartStatus(SessionBase session, int playerId, ItemComponent itemComponent, byte statusId, float elapsed = 0.0f)
        {
            itemComponent.status.id.Value = statusId;
            itemComponent.status.elapsed.Value = elapsed;
            switch (statusId)
            {
                case ItemStatusId.PrimaryUsing:
                    PrimaryUse(session, playerId, itemComponent);
                    break;
                case ItemStatusId.SecondaryUsing:
                    SecondaryUse();
                    break;
            }
        }

        /// <returns>State to switch to</returns>
        protected virtual byte? FinishStatus(ItemComponent item, InventoryComponent inventory, InputFlagProperty inputs)
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

        protected virtual void SecondaryUse() { }

        protected virtual void PrimaryUse(SessionBase session, int playerId, ItemComponent item) { }

        public void ModifyCommands(SessionBase session, InputFlagProperty commandsToModify) { }

        protected virtual bool HasSecondaryUse() { return false; }

        internal virtual void OnUnequip(SessionBase session, int playerId, ItemComponent itemComponent) { }
    }
}