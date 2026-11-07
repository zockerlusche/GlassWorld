// Sea in the D355 look: soft colour bands by water depth (shallow, middle, deep), a light foam
// line that breathes at the shore and a few slow glints. Presentation only.
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_opaque;

uniform sampler2D depth_texture : hint_depth_texture, filter_nearest;
uniform vec4 foam_color : source_color = vec4(0.84, 0.93, 0.9, 1.0);
uniform vec4 shallow_color : source_color = vec4(0.67, 0.82, 0.8, 1.0);
uniform vec4 middle_color : source_color = vec4(0.51, 0.75, 0.78, 1.0);
uniform vec4 deep_color : source_color = vec4(0.47, 0.71, 0.77, 1.0);
uniform float foam_depth = 0.12;
uniform float shallow_depth = 0.6;
uniform float middle_depth = 1.8;
uniform float band_softness = 0.08;
uniform float wave_speed = 0.35;
uniform float glint_amount = 0.6;

float hash(vec2 p) {
	return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
}

void fragment() {
	float scene_depth = texture(depth_texture, SCREEN_UV).r;
	vec4 world = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, scene_depth, 1.0);
	world.xyz /= world.w;
	float water_depth = clamp(-world.z + VERTEX.z, 0.0, 100.0);
	// Vertical depth is closer to what the eye reads in the top-down view.
	water_depth *= 0.7;

	float breathe = 0.5 + 0.5 * sin(TIME * wave_speed * 6.2831);
	float foam_edge = foam_depth * (0.7 + 0.6 * breathe);
	vec3 color = deep_color.rgb;
	color = mix(middle_color.rgb, color, smoothstep(middle_depth - band_softness * 4.0, middle_depth + band_softness * 4.0, water_depth));
	color = mix(shallow_color.rgb, color, smoothstep(shallow_depth - band_softness, shallow_depth + band_softness, water_depth));
	color = mix(foam_color.rgb, color, smoothstep(foam_edge - 0.03, foam_edge + 0.03, water_depth));

	// Sparse glints: small cells that light up now and then.
	vec3 wp = (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
	vec2 cell = floor(wp.xz * 0.8);
	float h = hash(cell);
	float blink = smoothstep(0.92, 1.0, sin(TIME * 0.6 + h * 40.0));
	vec2 local = fract(wp.xz * 0.8) - 0.5;
	float dash = smoothstep(0.06, 0.0, abs(local.y)) * smoothstep(0.3, 0.1, abs(local.x));
	color = mix(color, vec3(1.0), dash * blink * glint_amount * step(0.7, h) * smoothstep(0.4, 1.2, water_depth));

	ALBEDO = color;
}
