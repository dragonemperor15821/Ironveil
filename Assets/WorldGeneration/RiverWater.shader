
Shader "Ironveil/RiverWater"
{
    Properties
    {
        _ShallowColor ("Shallow Water", Color) = (0.12, 0.55, 0.78, 0.82)
        _DeepColor ("Deep Water", Color) = (0.025, 0.20, 0.38, 0.92)
        _FlowSpeed ("Flow Speed", Float) = 0.12
        _WaveScale ("Wave Scale", Float) = 18
        _RippleStrength ("Ripple Strength", Range(0, 0.3)) = 0.08
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
        }

        Pass
        {
            Name "RiverWater"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _ShallowColor;
                float4 _DeepColor;
                float _FlowSpeed;
                float _WaveScale;
                float _RippleStrength;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionHCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float2 uv = input.uv;

                // Scroll the pattern along the river's length.
                uv.y += _Time.y * _FlowSpeed;

                float waveA = sin(uv.y * _WaveScale);
                float waveB = sin(uv.y * _WaveScale * 0.57 + uv.x * 8.0);

                float ripple = (waveA + waveB) * 0.5;
                float blend = saturate(0.5 + ripple * 0.35);

                float3 water = lerp(
                    _DeepColor.rgb,
                    _ShallowColor.rgb,
                    blend
                );

                water += ripple * _RippleStrength;

                float alpha = lerp(
                    _DeepColor.a,
                    _ShallowColor.a,
                    blend
                );

                return half4(saturate(water), alpha);
            }
            ENDHLSL
        }
    }
}
