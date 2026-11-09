// Fresh water of the island (B2 R20) in the D355 look: lakes lie calm with a slow shimmer, streams
// show light stripes that move in the direction the core gave them (UV = flow x, z), swamp water is the
// murky colour from the data and barely moves. The colour comes from the vertex (terrain look data);
// the stripe and shimmer values are set from content/look/terrain.json (water_shader), the defaults here
// only show the shader in the editor.
// Presentation only.
shader_type spatial;
render_mode unshaded, cull_disabled;

uniform float stripe_scale = 1.6;
uniform float stripe_speed = 0.6;
uniform float stripe_strength = 0.12;
uniform float shimmer_speed = 0.7;
uniform float shimmer_strength = 0.04;
// Tint of the day phase (DaylightLook, content/look/daylight.json water_light): the water is unlit,
// so the night darkens it here; white by default.
global uniform vec4 water_light;

float hash(vec2 p) {
	return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
}

varying vec3 world_pos;

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	vec3 color = COLOR.rgb;
	vec2 flow = UV;
	float moving = clamp(length(flow) * 4.0, 0.0, 1.0);
	// Stripes across the flow, travelling along it.
	float along = dot(world_pos.xz, normalize(flow + vec2(1e-4))) * stripe_scale - TIME * stripe_speed;
	float stripes = smoothstep(0.55, 0.95, 0.5 + 0.5 * sin(along * 6.2831 + hash(floor(world_pos.xz)) * 0.8));
	// Still water: a slow, soft shimmer.
	float shimmer = 0.5 + 0.5 * sin(TIME * shimmer_speed + world_pos.x * 0.9 + world_pos.z * 0.6);
	color += vec3(1.0) * (stripes * stripe_strength * moving + shimmer * shimmer_strength * (1.0 - moving));
	ALBEDO = color * water_light.rgb;
}
