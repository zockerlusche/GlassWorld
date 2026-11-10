// Fresh water of the island (B2 R20, W1724) in the D355 look. Streams show flow lines: long light
// streaks that drift downstream, each lane at its own pace and wandering a little sideways - small
// currents instead of even stripes. Lakes lie calm with a slow shimmer and carry wave crests by the
// weather (water_waves.gdshaderinc). Swamp water is the murky colour from the data.
// The colour comes from the vertex (terrain look data). UV is the flow direction the core gave the water
// (0, 0 for still water). UV2 is the place on a stream band: x metres along its centre line, y across it
// (-1..1, 0 on the centre line), so the lines run on without a seam from face to face.
// The values are set from content/look/terrain.json (water_shader, waves); the defaults here only show
// the shader in the editor. Presentation only.
shader_type spatial;
render_mode unshaded, cull_disabled;

#include "res://shaders/water_waves.gdshaderinc"

uniform float flow_lanes = 5.0;
uniform float flow_line_length = 1.8;
uniform float flow_speed = 0.7;
uniform float flow_speed_spread = 0.6;
uniform float flow_bend = 0.8;
uniform float flow_strength = 0.16;
uniform float shimmer_speed = 0.7;
uniform float shimmer_strength = 0.04;
uniform float wave_strength = 0.7;
uniform float wave_share = 0.6;
// Tint of the day phase (DaylightLook, content/look/daylight.json water_light): the water is unlit,
// so the night darkens it here; white by default.
global uniform vec4 water_light;

varying vec3 world_pos;

// Smooth value noise, 0..1.
float flow_noise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	float a = wave_hash(i);
	float b = wave_hash(i + vec2(1.0, 0.0));
	float c = wave_hash(i + vec2(0.0, 1.0));
	float d = wave_hash(i + vec2(1.0, 1.0));
	return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

// One set of flow lines on a stream band (x metres along, y -1..1 across), 0..1. The band is split into
// lanes; a lane drifts as a whole at its own speed (so nothing shears), and a line fades to nothing
// before the edge of its lane (so nothing pops where the lanes wander).
float flow_lines(vec2 band, float time, float lanes, float seed) {
	float wander = (flow_noise(vec2(band.x * 0.4 + seed, time * 0.07 + seed)) - 0.5) * flow_bend;
	float v = (band.y * 0.5 + 0.5) * lanes + wander;
	float lane = floor(v);
	float fv = fract(v) - 0.5;
	float pace = wave_hash(vec2(lane, seed + 1.0));
	float speed = flow_speed * (1.0 + flow_speed_spread * (pace - 0.5));
	float s = (band.x - time * speed) / max(flow_line_length, 0.01) + pace * 31.0;
	// Where along the lane a line lies, how wide it is there and how it sways within the lane.
	float there = smoothstep(0.5, 0.72, flow_noise(vec2(s, lane * 3.7 + seed)));
	float width = 0.1 + 0.2 * flow_noise(vec2(s * 0.6 + 5.0, lane * 1.9 + seed));
	float wiggle = (flow_noise(vec2(s * 1.7 + 9.0, lane * 2.3 + seed)) - 0.5) * 0.3;
	float body = 1.0 - smoothstep(width * 0.5, width, abs(fv + wiggle));
	return there * body;
}

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	vec3 color = COLOR.rgb;
	float moving = clamp(length(UV) * 4.0, 0.0, 1.0);
	float still = 1.0 - moving;
	if (moving > 0.0) {
		// Running water: two sets of flow lines, a coarse one and a finer, faster one.
		vec2 band = UV2;
		float lines = max(flow_lines(band, TIME, flow_lanes, 0.0), 0.6 * flow_lines(band, TIME * 1.31, flow_lanes * 1.7 + 0.5, 23.0));
		float inside = 1.0 - smoothstep(0.7, 1.0, abs(band.y));
		color += vec3(lines * inside * flow_strength * moving);
	}
	if (still > 0.0) {
		// Still water: a slow, soft shimmer, and wave crests by the weather.
		float shimmer = 0.5 + 0.5 * sin(TIME * shimmer_speed + world_pos.x * 0.9 + world_pos.z * 0.6);
		color += vec3(shimmer * shimmer_strength * still);
		float crest = wave_crests(world_pos.xz, TIME, wave_share);
		color = mix(color, vec3(0.93, 0.97, 0.98), crest * wave_strength * still);
	}
	ALBEDO = color * water_light.rgb;
}
