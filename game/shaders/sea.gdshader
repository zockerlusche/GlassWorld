// Sea in the D355 look: soft colour bands by water depth (shallow, middle, deep), a light foam
// line that breathes at the shore, a few slow glints and wave crests by the weather (W1724,
// water_waves.gdshaderinc: none in calm weather, a few small ones, many large ones in a storm, and the
// foam line breathes wider with them). Presentation only.
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_opaque;

#include "res://shaders/water_waves.gdshaderinc"

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
// Wave crests (content/look/terrain.json waves): how strongly a crest lightens the water, and how much
// wider the foam line at the shore breathes in full waves.
uniform float wave_strength = 0.7;
uniform float wave_shore_foam = 1.0;
// Tint of the day phase (DaylightLook, content/look/daylight.json water_light): the water is unlit,
// so the night darkens it here; white by default.
global uniform vec4 water_light;

float hash(vec2 p) {
	return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
}

// Same shore line as the ground (island_ground.gdshader); bands near the shore use the distance to it,
// so the coast is a smooth curve instead of following the terrain triangles.
uniform bool use_shore_line = true;

float shore_x(float z) {
	return 7.5 + 3.2 * sin(0.13 * z + 0.5) + 1.1 * sin(0.33 * z);
}

void fragment() {
	float scene_depth = texture(depth_texture, SCREEN_UV).r;
	vec4 world = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, scene_depth, 1.0);
	world.xyz /= world.w;
	float water_depth = clamp(-world.z + VERTEX.z, 0.0, 100.0);
	// Vertical depth is closer to what the eye reads in the top-down view.
	water_depth *= 0.7;

	float breathe = 0.5 + 0.5 * sin(TIME * wave_speed * 6.2831);
	float waves_now = clamp(water_waves.x, 0.0, 1.0);
	float foam_edge = foam_depth * (0.7 + 0.6 * breathe) * (1.0 + wave_shore_foam * waves_now);
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
	float dash = (1.0 - smoothstep(0.0, 0.06, abs(local.y))) * (1.0 - smoothstep(0.1, 0.3, abs(local.x)));
	color = mix(color, vec3(1.0), dash * blink * glint_amount * step(0.7, h) * smoothstep(0.4, 1.2, water_depth));

	// Wave crests by the weather, away from the foam at the shore.
	float crest = wave_crests(wp.xz, TIME, 1.0) * smoothstep(0.25, 0.9, water_depth);
	color = mix(color, foam_color.rgb, crest * wave_strength);

	if (use_shore_line) {
		float seaward = wp.x - shore_x(wp.z);
		float aa = max(fwidth(seaward), 0.002);
		float foam_band = 0.45 + 0.25 * breathe;
		vec3 near_shore = mix(foam_color.rgb, shallow_color.rgb, smoothstep(foam_band - aa, foam_band + aa, seaward));
		color = mix(near_shore, color, smoothstep(2.2, 3.4, seaward));
	}

	ALBEDO = color * water_light.rgb;
}
