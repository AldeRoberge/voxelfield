﻿Shader "Custom/Foliage" {
    Properties {
        _Color("Color", Color) = (1,1,1,1)
        _MainTex("Albedo (RGB)", 2D) = "white" {}
        _Cutoff("Alpha Cutoff", Range(0,0.9)) = 0.5
        
        _LateralScale ("Lateral Scale", Float) = 32.0
        _VerticalScale ("Vertical Scale", Float) = 0.1
    }

    SubShader {
        Tags { 
            "IgnoreProjector" = "True"
            "RenderType" = "TransparentCutout"
        }
        
        LOD 200
        Cull off
        
        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows addshadow alphatest:_Cutoff vertex:vert
        #pragma target 3.0
        
        float _LateralScale, _VerticalScale;

        void vert (inout appdata_full v) {
            float3 worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
            v.vertex.x += sin(_Time * _LateralScale + worldPos.x) * _VerticalScale * v.texcoord.y;
            v.vertex.y += cos(_Time * _LateralScale + worldPos.y) * _VerticalScale * v.texcoord.y;
            v.vertex.z += cos(_Time * _LateralScale + worldPos.z) * _VerticalScale * v.texcoord.y;
        }

        sampler2D _MainTex;

        struct Input {
            float2 uv_MainTex;
        };

        fixed4 _Color;

        UNITY_INSTANCING_BUFFER_START(Props)
        UNITY_INSTANCING_BUFFER_END(Props)

        void surf(Input IN, inout SurfaceOutputStandard o) {
            fixed4 c = tex2D(_MainTex, IN.uv_MainTex) * _Color;
            o.Albedo = c.rgb;
            // Multiply alpha by value between 0 and 1
            o.Alpha = c.a;
        }
        ENDCG
    }
}