#version {VERSION}
#ifdef GL_ES
precision mediump float;
#endif

// Burnt orange "after the nuke" grade: the picture is remapped onto a dark-red to orange-white ramp,
// highlights are blown out, a warm haze lifts the shadows and the corners darken.
uniform float Strength;
uniform float Pulse;
uniform sampler2D SourceTexture;
out vec4 fragColor;

void main()
{
	vec4 c = texelFetch(SourceTexture, ivec2(gl_FragCoord.xy), 0);
	float lum = dot(c.rgb, vec3(0.299, 0.587, 0.114));

	vec3 shadow = vec3(0.22, 0.04, 0.02);
	vec3 mid = vec3(0.95, 0.42, 0.12);
	vec3 high = vec3(1.0, 0.93, 0.72);
	vec3 graded = lum < 0.5 ? mix(shadow, mid, lum * 2.0) : mix(mid, high, lum * 2.0 - 1.0);
	graded += pow(lum, 3.0) * vec3(0.5, 0.35, 0.15);
	graded = mix(graded, vec3(1.0, 0.55, 0.25), 0.12 + 0.05 * Pulse);

	vec2 uv = gl_FragCoord.xy / vec2(textureSize(SourceTexture, 0));
	float vignette = 1.0 - 0.55 * smoothstep(0.35, 0.8, distance(uv, vec2(0.5)));
	graded *= vignette;

	fragColor = vec4(mix(c.rgb, graded, Strength), c.a);
}
