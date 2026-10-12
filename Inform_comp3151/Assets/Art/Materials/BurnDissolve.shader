// Inkform burn-dissolve — started as a port of "Pixelated burn" (godotshaders.com/shader/pixelated-burn/,
// CC0 by Demyr) to URP 2D sprites, since grown into layered fire. A circle of fire expands from
// the ignition point; reading outward from the burn line, every chunk of the sprite is in one of
// four zones of the signed distance d = dist - _BurnFront:
//
//     d < 0                      burned through — transparent
//     0 ≤ d < _RimWidth          the white-hot rim: the line where the body is being eaten
//     d < _BorderWidth (+ lick)  the fire band — dithered, posterized char → red → orange → hot
//                                ramp, its outer edge licked by upward-scrolling flame tongues
//     d < band + _ScorchWidth    scorch — the body is pulled toward a deep ember red before the
//                                fire arrives (bright art darkens, the black slime smolders),
//                                with stray embers twinkling in it
//     beyond                     untouched sprite
//
// Everything moves on a stepped clock (_FlickerFps) and in _Chunk-sized pixel blocks, so the
// fire animates like hand-drawn pixel frames rather than smooth shader noise. The clock is
// _BurnTime, driven by BurnAwayFx on the presentation clock — the flames freeze under a user
// pause along with the rest of the death.
//
// What stays from the original: the noisy distance field (organic hole edge), the pixel
// snapping, the posterized fire ramp. Port notes: position/radius are _BurnOrigin/_BurnFront;
// pixel_size is _Chunk (texel-driven); the noise texture is in-shader value-noise fBm; the
// colorCurve texture is the in-shader 4-stop ramp; distance lives in "aspect space" (u
// pre-multiplied by width/height) so the circle is round in world units on non-square sprites.
Shader "Inkform/BurnDissolve"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)

        _BurnOrigin ("Burn Origin (aspect-space UV)", Vector) = (0.5, 0.5, 0, 0)
        _Aspect ("Aspect (width / height)", Float) = 1
        _BurnFront ("Burn Radius", Float) = -1
        [HideInInspector] _BurnTime ("Burn Time (driven by BurnAwayFx)", Float) = 0

        [Header(Shape)]
        _BorderWidth ("Fire Band Width", Range(0.01, 1)) = 0.2
        _BurnMult ("Hole Edge Noise", Range(0, 0.5)) = 0.34
        _NoiseScale ("Noise Scale", Range(1, 64)) = 6
        _Chunk ("Pixel Chunk", Range(1, 8)) = 2
        _RimWidth ("White-hot Rim Width", Range(0.005, 0.2)) = 0.035
        _ScorchWidth ("Scorch Width (ahead of the fire)", Range(0, 0.6)) = 0.16

        [Header(Flames)]
        _FlameLick ("Flame Tongue Reach", Range(0, 0.4)) = 0.12
        _FlameSpeed ("Flame Rise Speed", Range(0, 6)) = 2.2
        _FlickerFps ("Flicker Frame Rate", Range(2, 30)) = 12
        _BlendSteps ("Fire Ramp Steps", Range(2, 16)) = 6
        _Dither ("Ramp Dither", Range(0, 1)) = 1
        _EdgeBias ("Edge Color Bias", Range(0.05, 2)) = 0.1
        _Glow ("Glow Boost (blooms with HDR)", Range(1, 4)) = 1.8

        [Header(Embers and Scorch)]
        _EmberDensity ("Ember Density", Range(0, 0.5)) = 0.14
        _ScorchColor ("Scorch Tint", Color) = (0.42, 0.09, 0.04, 1)

        [Header(Fire Ramp)]
        _RampChar ("Ramp: Char", Color) = (0.1, 0.05, 0.03, 1)
        _RampRed ("Ramp: Deep Red", Color) = (0.55, 0.1, 0.05, 1)
        _RampOrange ("Ramp: Orange", Color) = (0.95, 0.45, 0.08, 1)
        _RampHot ("Ramp: Hot", Color) = (1, 0.9, 0.55, 1)
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
                float4 _MainTex_TexelSize;   // zw = texture width/height, for pixel snapping
                float4 _Color;
                float4 _BurnOrigin;
                float  _Aspect;
                float  _BurnFront;
                float  _BurnTime;
                float  _BorderWidth;
                float  _BurnMult;
                float  _NoiseScale;
                float  _Chunk;
                float  _RimWidth;
                float  _ScorchWidth;
                float  _FlameLick;
                float  _FlameSpeed;
                float  _FlickerFps;
                float  _BlendSteps;
                float  _Dither;
                float  _EdgeBias;
                float  _Glow;
                float  _EmberDensity;
                half4  _ScorchColor;
                half4  _RampChar;
                half4  _RampRed;
                half4  _RampOrange;
                half4  _RampHot;
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
                return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453);
            }

            // Smooth value noise — stands in for the reference's Perlin NoiseTexture2D
            float ValueNoise(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                float a = Hash(i);
                float b = Hash(i + float2(1.0, 0.0));
                float c = Hash(i + float2(0.0, 1.0));
                float d = Hash(i + float2(1.0, 1.0));
                return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
            }

            // Three octaves: big blobs for the silhouette of the burn, small grain for its fringe
            float Fbm(float2 p)
            {
                float v = ValueNoise(p) * 0.57;
                v += ValueNoise(p * 2.03 + 17.1) * 0.29;
                v += ValueNoise(p * 4.11 + 41.7) * 0.14;
                return v;
            }

            // 4×4 ordered dither, per chunk: ramp steps interleave in a pixel-art checker
            // instead of banding in flat rings
            static const float kBayer[16] = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };

            float Bayer4(float2 c)
            {
                int2 i = int2(fmod(c, 4.0));
                return kBayer[i.y * 4 + i.x] / 16.0;
            }

            // The colorCurve gradient texture, rebuilt in-shader: 0 = cold char → 1 = white-hot
            half3 FireRamp(float t)
            {
                return t < 1.0 / 3.0
                    ? lerp(_RampChar.rgb, _RampRed.rgb, t * 3.0)
                    : t < 2.0 / 3.0
                        ? lerp(_RampRed.rgb, _RampOrange.rgb, t * 3.0 - 1.0)
                        : lerp(_RampOrange.rgb, _RampHot.rgb, t * 3.0 - 2.0);
            }

            half4 Frag(Varyings IN) : SV_Target
            {
                half4 sprite = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv) * IN.color * _Color;

                // snapped uv: every pixel of a chunk shares one distance and one noise value —
                // the whole effect moves in blocky jumps
                float  chunkSize = max(1.0, _Chunk);
                float2 px        = floor(IN.uv * _MainTex_TexelSize.zw);
                float2 chunk     = floor(px / chunkSize);
                float2 snappedUV = (chunk * chunkSize + 0.5 * chunkSize) * _MainTex_TexelSize.xy;

                // stepped clock: the fire redraws at a pixel-animation frame rate
                float tick = floor(_BurnTime * _FlickerFps);
                float t    = tick / _FlickerFps;

                // burn-through field: static noise, so the hole edge is organic but never jitters
                float dist = distance(float2(snappedUV.x * _Aspect, snappedUV.y), _BurnOrigin.xy)
                           + Fbm(snappedUV * _NoiseScale) * _BurnMult;
                float d = dist - _BurnFront;

                // flame tongues: fBm scrolling upward pushes the fire band outward in licks that
                // rise and flicker — they color the body, they never burn holes
                float2 flameUV = float2(snappedUV.x * _Aspect, snappedUV.y) * _NoiseScale * 1.6;
                float  lick    = Fbm(flameUV + float2(0.0, -t * _FlameSpeed));
                float  dFlame  = d - lick * _FlameLick;

                // fire band heat: 1 at the burn line → 0 at the band's outer edge, dithered and
                // posterized into hard color steps
                float heat = saturate(1.0 - dFlame / _BorderWidth);
                float dith = (Bayer4(chunk) - 0.5) * _Dither;
                float steps = max(2.0, _BlendSteps);
                float heatStep = heat > 0.0 ? saturate(floor(heat * steps + dith) / steps) : 0.0;

                // scorch ahead of the fire: the body is pulled toward ember red before it catches —
                // a bright sprite reads as charring, a dark one as starting to glow; luminance is
                // kept a little so eyes and outlines survive into the scorch
                float scorch = saturate(1.0 - (dFlame - _BorderWidth) / max(_ScorchWidth, 1e-4));
                scorch = floor(scorch * 4.0 + dith) / 4.0;
                half  lum = dot(sprite.rgb, half3(0.299, 0.587, 0.114));
                half3 body = lerp(sprite.rgb, _ScorchColor.rgb * (0.7 + lum * 0.6), saturate(scorch) * 0.75);

                // embers: stray chunks in the scorch that flare and die on the frame clock
                float ember = step(1.0 - _EmberDensity * saturate(scorch), Hash(chunk + floor(tick * 0.5) * 13.7));
                body = lerp(body, _RampOrange.rgb * _Glow, ember * step(heatStep, 0.0));

                // white-hot rim right at the burn line, brightest where the flames lick hardest
                float rim = 1.0 - step(_RimWidth * (0.6 + lick), d);

                // the band keeps its ramp colors (a little lift near the line); only the thin rim gets
                // the full glow, so the hot edge pops without the band clipping to flat white
                half3 fire = FireRamp(saturate(heatStep * 0.92 + rim * 0.5));
                fire *= 1.0 + (_Glow - 1.0) * saturate(rim + heatStep * heatStep * 0.3);

                // edge bias: the fire color claims the body quickly as the front arrives
                float claim = heatStep > 0.0 ? pow(heatStep, _EdgeBias) : 0.0;
                sprite.rgb = lerp(body, fire, saturate(claim + rim));

                // inside the radius: burned through
                sprite.a *= step(0.0, d);
                return sprite;
            }
            ENDHLSL
        }
    }
}
