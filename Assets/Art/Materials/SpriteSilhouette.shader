// Draws a sprite as one flat colour wherever its texture has alpha. The character highlight outline
// uses this on offset copies of the body sprite, so any art — any animation frame, future enemy
// sprites — gets an outline with no hand-drawn outline assets. The colour comes from the
// SpriteRenderer's colour (vertex colour), exactly like Sprites/Default.
Shader "PuckHero/SpriteSilhouette"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        // This Unity feeds SpriteRenderer.color through this per-renderer property (not the vertex
        // colour) on the URP 2D path — verified 2026-08-08: without it every silhouette rendered white.
        [PerRendererData] _RendererColor ("RendererColor", Color) = (1,1,1,1)
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
            "PreviewType" = "Plane"
            "CanUseSpriteAtlas" = "True"
        }

        Cull Off
        Lighting Off
        ZWrite Off
        Blend One OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata_t
            {
                float4 vertex : POSITION;
                fixed4 color : COLOR;
                float2 texcoord : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                fixed4 color : COLOR;
                float2 texcoord : TEXCOORD0;
            };

            sampler2D _MainTex;
            fixed4 _RendererColor;

            v2f vert(appdata_t IN)
            {
                v2f OUT;
                OUT.vertex = UnityObjectToClipPos(IN.vertex);
                OUT.texcoord = IN.texcoord;
                OUT.color = IN.color * _RendererColor; // whichever path carries the colour, the other is white
                return OUT;
            }

            fixed4 frag(v2f IN) : SV_Target
            {
                fixed alpha = tex2D(_MainTex, IN.texcoord).a * IN.color.a;
                return fixed4(IN.color.rgb * alpha, alpha); // premultiplied, flat colour
            }
            ENDCG
        }
    }
}
