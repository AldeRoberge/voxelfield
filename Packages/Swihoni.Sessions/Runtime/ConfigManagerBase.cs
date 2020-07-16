using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Console;
using Swihoni.Components;
using Swihoni.Sessions.Components;
using UnityEngine;

namespace Swihoni.Sessions
{
    public class ConfigAttribute : Attribute
    {
        public string Name { get; }
        public bool IsSession { get; }

        public ConfigAttribute(string name, bool isSession = false)
        {
            Name = name;
            IsSession = isSession;
        }
    }

    [CreateAssetMenu(fileName = "Config", menuName = "Session/Config", order = 0)]
    public class ConfigManagerBase : ScriptableObject
    {
        private static Dictionary<Type, (PropertyBase, ConfigAttribute)> TypeToConfig { get; set; }
        private static Dictionary<string, (PropertyBase, ConfigAttribute)> NameToConfig { get; set; }
        
        public static ConfigManagerBase Singleton { get; private set; }

        [Config("tick_rate", true)] public TickRateProperty tickRate = new TickRateProperty(60);
        [Config("allow_cheats", true)] public AllowCheatsProperty allowCheats = new AllowCheatsProperty();
        [Config("mode_id", true)] public ModeIdProperty modeId = new ModeIdProperty();
        [Config("respawn_duration")] public TimeUsProperty respawnDuration = new TimeUsProperty();
        [Config("player_health")] public ByteProperty playerHealth = new ByteProperty(100);

        [Config("fov")] public ByteProperty fov = new ByteProperty(60);
        [Config("target_fps")] public UShortProperty targetFps = new UShortProperty(200);
        [Config("volume")] public FloatProperty volume = new FloatProperty(0.5f);
        [Config("sensitivity")] public FloatProperty sensitivity = new FloatProperty(2.0f);
        [Config("crosshair_thickness")] public FloatProperty crosshairThickness = new FloatProperty(1.0f);
        
        public static void Initialize()
        {
            Singleton = Instantiate(Resources.Load<ConfigManagerBase>("Config"));
            if (!Singleton) throw new Exception("No config asset was found in resources");

            NameToConfig = new Dictionary<string, (PropertyBase, ConfigAttribute)>();
            TypeToConfig = new Dictionary<Type, (PropertyBase, ConfigAttribute)>();

            var names = new Stack<string>();
            void Recurse(ElementBase element)
            {
                bool isConfig = element.TryAttribute(out ConfigAttribute config);
                if (isConfig)
                {
                    switch (element)
                    {
                        case ComponentBase component:
                            names.Push(config.Name);
                            foreach (ElementBase childElement in component)
                                Recurse(childElement);
                            names.Pop();
                            break;
                        case PropertyBase property:
                            string fullName = string.Join(".", names.Append(config.Name));
                            NameToConfig.Add(fullName, (property, config));
                            if (config.IsSession)
                            {
                                TypeToConfig.Add(property.GetType(), (property, config));
                                ConsoleCommandExecutor.SetCommand(fullName, SessionBase.IssueCommand);
                            }
                            else
                            {
                                ConsoleCommandExecutor.SetCommand(fullName, HandleArgs);
                            }
                            break;
                    }
                }
            }
            foreach (FieldInfo field in Singleton.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                var element = (ElementBase) field.GetValue(Singleton);
                element.Field = field;
                Recurse(element);
            }
        }
        
        public static void HandleArgs(IReadOnlyList<string> split)
        {
            if (NameToConfig.TryGetValue(split[0], out (PropertyBase, ConfigAttribute) tuple))
            {
                switch (split.Count)
                {
                    case 2:
                        tuple.Item1.TryParseValue(split[1]);
                        Debug.Log($"Set {split[0]} to {split[1]}");
                        break;
                    case 1 when tuple.Item1 is BoolProperty boolProperty:
                        boolProperty.Value = true;
                        Debug.Log($"Set {split[0]}");
                        break;
                }
            }
        }

        public static void UpdateConfig(ComponentBase session)
        {
            foreach (ElementBase element in session.Elements)
                if (element is PropertyBase property && TypeToConfig.TryGetValue(property.GetType(), out (PropertyBase, ConfigAttribute) tuple))
                    property.SetFromIfWith(tuple.Item1);
        }
    }
}