Shader "ACC/LightweightLakeWater"
{
    Properties
    {
        _WaterColor ("Water Color", Color) = (0.05, 0.35, 0.45, 0.75)
        _WaveTex ("Wave Normal/Noise", 2D) = "bump" {}
        _WaveScale ("Wave Scale", Float) = 0.15
        _WaveSpeed ("Wave Speed", Vector) = (0.03, 0.02, -0.02, -0.015)
        _WaveStrength ("Wave Strength", Range(0, 1)) = 0.25
        _Smoothness ("Smoothness", Range(0, 1)) = 0.8
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
        }

        Pass
        {
            Name "Water"
            Tags
            {
                "LightMode" = "UniversalForward"
            }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Back

            HLSLPROGRAM

            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            CBUFFER_START(UnityPerMaterial)

                float4 _WaterColor;
                float4 _WaveTex_ST;
                float4 _WaveSpeed;

                float _WaveScale;
                float _WaveStrength;
                float _Smoothness;

            CBUFFER_END

            TEXTURE2D(_WaveTex);
            SAMPLER(sampler_WaveTex);

            Varyings vert(Attributes input)
            {
                Varyings output;

                output.positionCS =
                    TransformObjectToHClip(input.positionOS.xyz);

                output.uv = TRANSFORM_TEX(input.uv, _WaveTex);

                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float2 uv = input.uv;

                float2 movement1 =
                    _Time.y * _WaveSpeed.xy;

                float2 movement2 =
                    _Time.y * _WaveSpeed.zw;

                float2 uv1 =
                    uv * _WaveScale + movement1;

                float2 uv2 =
                    uv * (_WaveScale * 1.7) + movement2;

                float3 wave1 =
                    SAMPLE_TEXTURE2D(
                        _WaveTex,
                        sampler_WaveTex,
                        uv1
                    ).xyz * 2.0 - 1.0;

                float3 wave2 =
                    SAMPLE_TEXTURE2D(
                        _WaveTex,
                        sampler_WaveTex,
                        uv2
                    ).xyz * 2.0 - 1.0;

                float wave =
                    (wave1.x + wave2.y) * 0.5;

                float3 finalColor =
                    _WaterColor.rgb;

                finalColor += wave * _WaveStrength;

                return half4(
                    finalColor,
                    _WaterColor.a
                );
            }

            ENDHLSL
        }
    }
}