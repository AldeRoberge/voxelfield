using System.Collections.Generic;
using System.Linq;
using System.Text;
using Swihoni.Components;
using Swihoni.Sessions.Components;
using Swihoni.Sessions.Config;
using Swihoni.Util.Interface;
using TMPro;
using UnityEngine;

namespace Swihoni.Sessions.Interfaces
{
    public class ChatInterface : SessionInterfaceBehavior
    {
        public const string ChatNameSeparator = ">";

        [SerializeField] private BufferedTextGui m_Text = default;
        [SerializeField] private CanvasGroup m_ChatCanvasGroup = default;
        [SerializeField] private float m_ShowChatLength = 4.0f;
        private TMP_InputField m_Input;
        private string m_WantedInput;
        private float m_TimeSinceLastChat;

        private readonly Queue<string> m_Chats = new();

        protected override void Awake()
        {
            m_Input = GetComponentInChildren<TMP_InputField>();
            base.Awake();
            m_Input.onEndEdit.AddListener(chatString =>
            {
                m_WantedInput = chatString;
                m_Input.text = string.Empty;
            });
        }

        public override void ModifyLocalTrusted(int localPlayerId, SessionBase session, Container commands)
        {
            if (string.IsNullOrEmpty(m_WantedInput)) return;

            commands.Require<ChatEntryProperty>().SetTo(m_WantedInput);
            m_WantedInput = null;
        }

        public override void Render(in SessionContext context)
        {
            if (NoInterrupting && InputProvider.GetInputDown(InputType.ToggleChat) && !m_Input.isFocused)
                ToggleInterfaceActive();
            m_ChatCanvasGroup.ignoreParentGroups = m_TimeSinceLastChat < m_ShowChatLength;
            m_TimeSinceLastChat += Time.deltaTime;
        }

        protected override void OnSetInterfaceActive(bool isActive)
        {
            if (isActive)
            {
                m_Input.enabled = true;
                m_Input.ActivateInputField();
                m_Input.Select();
            }
            else
            {
                m_Input.DeactivateInputField();
                m_Input.enabled = false;
            }
            m_TimeSinceLastChat = float.PositiveInfinity;
        }

        public override void SessionStateChange(bool isActive)
        {
            base.SessionStateChange(isActive);
            if (!isActive)
            {
                m_Chats.Clear();
                m_Text.Clear();
            }
        }

        public override void RenderVerified(in SessionContext context)
        {
            if (context.sessionContainer.WithPropertyWithValue(out ChatList chats))
                foreach (ChatEntryProperty chat in chats.List)
                {
                    m_Chats.Enqueue(chat.AsNewString());
                    RenderChats(context);
                    m_TimeSinceLastChat = 0.0f;
                }
        }

        private void RenderChats(in SessionContext context)
        {
            StringBuilder builder = m_Text.StartBuild();
            foreach (string[] split in m_Chats.Select(chat => chat.Split(new[] {' '}, 2)))
                context.Mode.AppendUsername(builder, context.GetPlayer(int.Parse(split[0]))).Append(ChatNameSeparator).Append(" ").Append(split[1]).Append("\n");
            builder.Commit(m_Text);
        }
    }
}