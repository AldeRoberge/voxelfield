using LiteNetLib.Utils;
using UnityEngine;

namespace Swihoni.Components
{
    public static class ComponentExtensions
    {
        public static Vector3 GetVector3(this NetDataReader reader)
            => new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());

        public static void Put(this NetDataWriter writer, in Vector3 vector)
        {
            writer.Put(vector.x);
            writer.Put(vector.y);
            writer.Put(vector.z);
        }

        public static Quaternion GetQuaternion(this NetDataReader reader)
            => new Quaternion(reader.GetFloat(), reader.GetFloat(), reader.GetFloat(), reader.GetFloat());

        public static void Put(this NetDataWriter writer, in Quaternion quaternion)
        {
            writer.Put(quaternion.x);
            writer.Put(quaternion.y);
            writer.Put(quaternion.z);
            writer.Put(quaternion.w);
        }

        public static void PutColor32(this NetDataWriter writer, in Color32 color)
        {
            writer.Put(color.r);
            writer.Put(color.g);
            writer.Put(color.b);
            writer.Put(color.a);
        }

        public static Color32 GetColor32(this NetDataReader reader)
            => new Color32(reader.GetByte(), reader.GetByte(), reader.GetByte(), reader.GetByte());

        public static void PutColor(this NetDataWriter writer, in Color color)
        {
            writer.Put(color.r);
            writer.Put(color.g);
            writer.Put(color.b);
            writer.Put(color.a);
        }

        public static Color GetColor(this NetDataReader reader)
            => new Color(reader.GetFloat(), reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
    }
}