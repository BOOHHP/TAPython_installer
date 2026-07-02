// iOS 26 液态玻璃折射：多层流动位移 + 边缘透镜厚度感（ps_2_0 预算内）。
// 编译：fxc /O3 /T ps_2_0 /E main /Fo Shaders/Refraction.ps Shaders/Refraction.hlsl

sampler2D implicitInput : register(s0);
float time     : register(c0);
float strength : register(c1);

float4 main(float2 uv : TEXCOORD) : COLOR
{
    // 多层正弦叠加，产生有机的液态流动
    float2 disp;
    disp.x = (sin(uv.y * 14.0 + time * 1.3) * 0.65 + sin(uv.y * 26.0 - time * 0.9) * 0.35) * strength;
    disp.y = cos(uv.x * 12.0 + time * 1.1) * strength;

    // 边缘折射增强，模拟玻璃透镜的厚度与放大感（液态玻璃核心）
    float2 c = uv - 0.5;
    disp += c * dot(c, c) * strength * 2.8;

    return tex2D(implicitInput, uv + disp);
}


