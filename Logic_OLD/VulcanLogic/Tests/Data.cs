using System;
using System.Collections.Generic;
using System.Text;

namespace PhysXCompute.Logic.VulcanLogic.Tests
{
    using System.Runtime.InteropServices;

    [StructLayout(LayoutKind.Sequential)]
    public struct Float3
    {
        public float X;
        public float Y;
        public float Z;

        public Float3(float x, float y, float z) => (X, Y, Z) = (x, y, z);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AABB
    {
        public Float3 MinPos;
        public Float3 MaxPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ObjectData
    {
        public AABB Bounds;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MortonPair
    {
        public uint MortonCode;
        public uint ObjectID;
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    public struct SceneBounds
    {
        [FieldOffset(0)] public Float3 SceneMin;
        [FieldOffset(12)] private float padding1;

        [FieldOffset(16)] public Float3 SceneInvSize;
        [FieldOffset(28)] private float padding2;

        [FieldOffset(24)] public uint ObjectCount_SceneBounds;
    }
}
