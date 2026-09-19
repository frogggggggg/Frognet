// Unity ShaderLab - Built-in Render Pipeline
// Builds on Custom/SeamlessTriplanarUnlit via the shared TriplanarCore.cginc.
Shader "Custom/BloodCellTriplanar"
{
    Properties
    {
        [Header(Color)]
        _Color ("Surface Color (ridges)", Color) = (0.80, 0.14, 0.13, 1)
        _DeepColor ("Deep Color (valleys)", Color) = (0.40, 0.04, 0.06, 1)
        _MainTex ("Detail Texture (optional)", 2D) = "white" {}
        _DetailStrength ("Detail Strength", Range(0,1)) = 0.0

        [Header(Surface Relief)]
        _NoiseScale ("Lump Scale", Float) = 3.0
        _BumpStrength ("Bump Strength", Range(0,4)) = 1.1
        _Detail ("Fine Detail", Range(0,1)) = 0.55
        _Lacunarity ("Lacunarity", Range(1.5,4)) = 2.1
        _Gain ("Gain", Range(0.2,0.8)) = 0.5
        _Displace ("Vertex Displacement", Range(0,0.5)) = 0.03

        [Header(Wetness)]
        _Glossiness ("Smoothness", Range(0,1)) = 0.70
        _GlossVariation ("Smoothness Variation", Range(0,0.5)) = 0.12
        _SpecTint ("Specular Tint (also tints reflections)", Color) = (0.17, 0.09, 0.09, 1)
        _Occlusion ("Cavity Shading", Range(0,1)) = 0.45

        [Header(Subsurface)]
        _SubsurfaceColor ("Subsurface Color", Color) = (1.0, 0.22, 0.13, 1)
        _SubsurfaceStrength ("Subsurface Strength", Range(0,3)) = 0.9
        _RimPower ("Rim Falloff", Range(0.5,8)) = 2.6
        _ThinGlow ("Thin-Area Glow", Range(0,1)) = 0.6

        [Header(Mapping)]
        _BlendSharpness ("Triplanar Blend Sharpness", Range(1,32)) = 6.0
        [KeywordEnum(Object, World)] _Space ("Mapping Space", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 300

        CGPROGRAM
        #pragma surface surf StandardSpecular fullforwardshadows vertex:vert addshadow
        #pragma target 4.0
        #pragma multi_compile_local _SPACE_OBJECT _SPACE_WORLD

        #include "TriplanarCore.cginc"

        sampler2D _MainTex;
        fixed4 _Color, _DeepColor, _SubsurfaceColor, _SpecTint;
        float _DetailStrength;
        float _NoiseScale, _BumpStrength, _Detail, _Lacunarity, _Gain, _Displace;
        float _Glossiness, _GlossVariation, _Occlusion;
        float _SubsurfaceStrength, _RimPower, _ThinGlow;
        float _BlendSharpness;

        struct Input
        {
            float3 worldPos;      // magic name: filled automatically
            float3 mapPos;        // position in the chosen mapping space
            float3 mapNrm;        // normal   in the chosen mapping space
            float3 wTangent;
            float3 wBinormal;
            float3 wNormal;
        };

        // Single place the relief height is defined, so vertex displacement and
        // fragment bump always agree on where the lumps are.
        float Height(float3 p)
        {
            return FBM3(p * _NoiseScale, _Gain, _Lacunarity, _Detail);
        }

        void vert(inout appdata_full v, out Input o)
        {
            UNITY_INITIALIZE_OUTPUT(Input, o);

        #ifdef _SPACE_WORLD
            o.mapPos = mul(unity_ObjectToWorld, v.vertex).xyz;
            o.mapNrm = UnityObjectToWorldNormal(v.normal);
        #else
            o.mapPos = v.vertex.xyz;
            o.mapNrm = v.normal;
        #endif

            // Push the silhouette around so the lumps are not purely a lighting
            // trick. Needs a dense mesh -- see the note about Unity's sphere.
            if (_Displace > 0.0)
            {
                float h = Height(o.mapPos) - 0.5;
                v.vertex.xyz += v.normal * h * _Displace;
            }

            // Tangent frame, so a world-space normal can be pushed back into the
            // tangent space that surface shaders expect from o.Normal.
            float3 wN = UnityObjectToWorldNormal(v.normal);
            float3 wT = UnityObjectToWorldDir(v.tangent.xyz);
            float3 wB = cross(wN, wT) * v.tangent.w * unity_WorldTransformParams.w;

            o.wNormal   = wN;
            o.wTangent  = wT;
            o.wBinormal = wB;
        }

        void surf(Input IN, inout SurfaceOutputStandardSpecular o)
        {
            float3 p = IN.mapPos;
            float3 n = normalize(IN.mapNrm);

            float h = Height(p);

            // Gradient by forward differences, then projected onto the tangent
            // plane so the bump slides the normal sideways instead of inflating
            // it. Costs 3 extra fBm evaluations per pixel.
            float e = 0.35 / max(_NoiseScale, 0.001);
            float3 grad = float3(
                Height(p + float3(e, 0, 0)) - h,
                Height(p + float3(0, e, 0)) - h,
                Height(p + float3(0, 0, e)) - h) / e;

            float3 tangentialGrad = grad - n * dot(grad, n);
            float3 bumpedMap = normalize(n - tangentialGrad * _BumpStrength);

        #ifdef _SPACE_WORLD
            float3 bumpedWorld = bumpedMap;
        #else
            float3 bumpedWorld = UnityObjectToWorldNormal(bumpedMap);
        #endif

            // World -> tangent (transpose of an orthonormal frame is its inverse).
            o.Normal = normalize(float3(
                dot(bumpedWorld, IN.wTangent),
                dot(bumpedWorld, IN.wBinormal),
                dot(bumpedWorld, IN.wNormal)));

            // Ridges read as thicker/brighter haemoglobin, valleys as thinner.
            fixed3 albedo = lerp(_DeepColor.rgb, _Color.rgb, h);

            if (_DetailStrength > 0.0)
            {
                fixed3 detail = TriplanarSample(_MainTex, p, n, _BlendSharpness).rgb;
                albedo *= lerp(fixed3(1,1,1), detail, _DetailStrength);
            }

            o.Albedo   = albedo;
            // Tinting specular red stops a blue sky reflection turning the cell purple.
            o.Specular = _SpecTint.rgb;

            // Break up the specular so the membrane looks wet rather than waxed.
            o.Smoothness = saturate(_Glossiness + (h - 0.5) * _GlossVariation);
            o.Occlusion  = lerp(1.0, saturate(h + 0.35), _Occlusion);

            // Cheap subsurface: rim-weighted glow, strongest where the surface is
            // thin. Not real transmission -- it does not know about light
            // direction -- but it sells translucency at a fraction of the cost.
            float3 V = normalize(_WorldSpaceCameraPos - IN.worldPos);
            float fres = pow(1.0 - saturate(dot(normalize(bumpedWorld), V)), _RimPower);
            float thin = lerp(1.0, saturate(1.0 - h), _ThinGlow);

            o.Emission = _SubsurfaceColor.rgb * _SubsurfaceStrength * fres * thin;
            o.Alpha = 1.0;
        }
        ENDCG
    }

    FallBack "Diffuse"
}
