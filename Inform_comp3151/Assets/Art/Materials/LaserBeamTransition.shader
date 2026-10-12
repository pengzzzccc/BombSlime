// Inkform laser beam transition — the beam assembling from (and dissolving into) particles,
// swept from the beam's top end toward its emitter base. Worn only while TimedVisibility plays
// an appear/disappear transition; the steady beam keeps its own material.
//
// Space: s runs 0 at the top end → 1 at the base, along the sprite's long local axis (_AxisX
// picks u or v, _BaseAtTop flips which end is the base). Grain cells are _GrainSize world units,
// measured through _Length/_Width (the beam's world size), so a 4-unit beam stretched from a
// 4-pixel sprite still breaks into square pixel motes instead of long slivers.
//
// d = _Front - s is the signed distance behind the sweep (positive = toward the top, already
// swept). Per cell, a hash re-rolled on a stepped clock and streaming toward the base decides
// whether that mote is lit this frame:
//   appear     (_Mode 0): density rises 0 → 1 over _Settle behind the front, a bright head rides
//                         the front, sparse sparks run _Lead ahead of it;
//   disappear  (_Mode 1): the mirror — density falls 1 → 0 behind the front as the beam breaks
//                         up from the top, the head marks where it is coming apart.
// Fully swept, every mote is lit with the plain sprite color, so the hand-off to the steady
// material is seamless.
Shader "Inkform/LaserBeamTransition"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)

        [HideInInspector] _Front ("Sweep Front (0 top → 1 base)", Float) = 0
        [HideInInspector] _Mode ("Mode (0 appear, 1 disappear)", Float) = 0
        [HideInInspector] _FxTime ("Time (driven by TimedVisibility)", Float) = 0
        [HideInInspector] _AxisX ("Long axis is u", Float) = 0
        [HideInInspector] _BaseAtTop ("Base at local top", Float) = 0
        [HideInInspector] _Length ("Beam length, world", Float) = 4
        [HideInInspector] _Width ("Beam width, world", Float) = 0.25

        _GrainSize ("Mote Size (world units)", Range(0.02, 0.25)) = 0.0625
        _Settle ("Settle Length (fraction of beam)", Range(0.02, 1)) = 0.35
        _HeadWidth ("Head Width (fraction of beam)", Range(0, 0.2)) = 0.05
        _Lead ("Spark Lead (fraction of beam)", Range(0, 0.5)) = 0.18
        _GrainFps ("Shimmer Frame Rate", Range(4, 60)) = 36
        _Flow ("Mote Flow Toward Base (cells per frame)", Range(0, 4)) = 1
        _Glow ("Head Brightness (blooms with HDR)", Range(1, 4)) = 1.6
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
            "PreviewType" = "Plane"
            "CanUseSpriteAtlas" = "True"
        }

        Cull Off
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            Name "Unlit"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float4 _Color;
                float  _Front;
                float  _Mode;
                float  _FxTime;
                float  _AxisX;
                float  _BaseAtTop;
                float  _Length;
                float  _Width;
                float  _GrainSize;
                float  _Settle;
                float  _HeadWidth;
                float  _Lead;
                float  _GrainFps;
                float  _Flow;
                float  _Glow;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                half4  color      : COLOR;      // SpriteRenderer.color rides in here
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv          : TEXCOORD0;
                half4  color       : COLOR;
            };

            Varyings Vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = TRANSFORM_TEX(IN.uv, _MainTex);
                OUT.color = IN.color;
                return OUT;
            }

            float Hash(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            half4 Frag(Varyings IN) : SV_Target
            {
                half4 sprite = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv) * IN.color * _Color;

                // beam space: a = along the long axis (0..1 local), c = across it
                float a = _AxisX > 0.5 ? IN.uv.x : IN.uv.y;
                float c = _AxisX > 0.5 ? IN.uv.y : IN.uv.x;
                float s = _BaseAtTop > 0.5 ? a : 1.0 - a;           // 0 = top end, 1 = base

                // world-sized pixel motes; every fragment of a mote shares s
                float  grain = max(_GrainSize, 1e-3);
                float2 cell  = floor(float2(s * _Length, c * _Width) / grain);
                float  sCell = (cell.x + 0.5) * grain / max(_Length, 1e-3);

                // stepped clock; motes stream toward the base as frames advance
                float tick = floor(_FxTime * _GrainFps);
                float h    = Hash(float2(cell.x - tick * _Flow, cell.y) + tick * 0.618);

                float d = _Front - sCell;                           // > 0: already swept
                float settle = max(_Settle, 1e-3);

                float density;
                float sparks = 0.0;
                if (_Mode < 0.5)
                {
                    // appear: motes gather behind the front; scattered sparks race ahead of it
                    density = d > 0.0 ? saturate(d / settle) : 0.0;
                    if (d <= 0.0 && _Lead > 0.0) sparks = saturate(1.0 + d / _Lead) * 0.22;
                }
                else
                {
                    // disappear: the beam breaks up from the top, motes thinning behind the front
                    density = d > 0.0 ? 1.0 - saturate(d / settle) : 1.0;
                }

                // the head: a dense, bright band riding the front
                float head = _HeadWidth > 0.0 ? saturate(1.0 - abs(d) / _HeadWidth) : 0.0;
                density = max(density, head * 0.85);

                float lit   = step(h, density);
                float spark = step(1.0 - sparks, h) * (1.0 - lit);

                // heat: motes near the front run hot toward the head color; settled ones are plain beam
                float heat = max(head, saturate(1.0 - abs(d) / settle) * (1.0 - step(1.0, density)) * 0.4);
                // the head and sparks are the laser's own color, brightened — never a foreign tint
                half3 hot = sprite.rgb * _Glow;
                half3 col = lerp(sprite.rgb, hot, max(heat, spark));

                sprite.rgb = col;
                sprite.a  *= saturate(lit + spark);
                return sprite;
            }
            ENDHLSL
        }
    }
}
