// HandOcclusion.shader — KitchenGuide
// Renders NOTHING to color; writes depth only. Applied to the tracked hand
// mesh, it makes the user's real hand (seen through passthrough) correctly
// mask virtual content behind it: organic hand occlusion with no visible
// hand model. Queue Geometry-1 so the depth is laid down just before the
// default opaque pass draws panels and props.
Shader "KitchenGuide/HandOcclusion"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry-1" }
        Pass
        {
            ColorMask 0
            ZWrite On
            ZTest LEqual
            Cull Back
        }
    }
    Fallback Off
}
