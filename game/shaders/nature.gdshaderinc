// Living nature (B2-5 R21/R24, content/look/nature.json), shared by the warm comic shader and the ink
// outline (so a swaying plant keeps its outline): the movable part of a vertex colour moves to the colour
// of the season, the weather and the look epoch (B2-5 R16), snow lies on faces that look up, plants sway
// in the wind. Everything comes from global
// parameters (project.godot [shader_globals]) that NatureLook sets per frame; no mesh is ever rebuilt.

// Per tint slot: the way from the base colour to the colour shown now (linear rgb).
global uniform vec4 season_tint_0;
global uniform vec4 season_tint_1;
global uniform vec4 season_tint_2;
global uniform vec4 season_tint_3;
global uniform vec4 season_tint_4;
global uniform vec4 season_tint_5;
global uniform vec4 season_tint_6;
global uniform vec4 season_tint_7;
// rgb: snow colour (linear), a: share of the ground covered by snow (0..1).
global uniform vec4 nature_snow;
// x: size of a snow patch in metres, y: softness of a patch edge, z: how far a face must look up.
global uniform vec4 nature_snow_shape;
// xy: unit direction the wind blows to on the ground (x, z), z: strength 0..1, w: phase of the swing.
global uniform vec4 nature_wind;
// x: phase per metre along the wind, y: share of the bend that is a steady lean.
global uniform vec4 nature_wind_shape;

// The vertex colour as shown now: weights and more are the shares of the movable palette colours in it
// (tint slots 0..3 in CUSTOM0, 4..7 in CUSTOM1).
vec3 season_color(vec3 color, vec4 weights, vec4 more) {
	vec3 moved = color + weights.r * season_tint_0.rgb + weights.g * season_tint_1.rgb + weights.b * season_tint_2.rgb + weights.a * season_tint_3.rgb;
	moved += more.r * season_tint_4.rgb + more.g * season_tint_5.rgb + more.b * season_tint_6.rgb + more.a * season_tint_7.rgb;
	return max(moved, vec3(0.0));
}

// A model vertex moved by the wind: nothing at the foot, top_m at the height height_m (both in model
// units, so the model's size scales the sway). A steady lean with the wind plus a slow swing of two whole
// harmonics; the phase runs over the land as a wave, so neighbours do not swing in step.
vec3 sway(vec3 point, mat4 model, float top_m, float height_m) {
	if (top_m <= 0.0 || nature_wind.z <= 0.0) {
		return point;
	}
	vec3 origin = model[3].xyz;
	float bend = clamp(point.y / max(height_m, 0.01), 0.0, 1.0);
	bend *= bend;
	float phase = nature_wind.w - dot(origin.xz, nature_wind.xy) * nature_wind_shape.x;
	float swing = 0.7 * sin(phase) + 0.3 * sin(2.0 * phase + 1.3);
	float push = nature_wind.z * (nature_wind_shape.y + (1.0 - nature_wind_shape.y) * swing);
	// The offset lies along the wind in the world; brought into model space by the rotation alone.
	vec3 along = vec3(nature_wind.x, 0.0, nature_wind.y);
	vec3 local = (vec4(along, 0.0) * model).xyz / max(length(model[0].xyz), 0.0001);
	return point + local * (top_m * bend * push);
}

float nature_hash(vec2 p) {
	vec3 q = fract(vec3(p.x, p.y, p.x) * 0.1031);
	q += dot(q, q.yzx + 33.33);
	return fract((q.x + q.y) * q.z);
}

float nature_noise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	float a = nature_hash(i);
	float b = nature_hash(i + vec2(1.0, 0.0));
	float c = nature_hash(i + vec2(0.0, 1.0));
	float d = nature_hash(i + vec2(1.0, 1.0));
	return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

// The colour under the snow cover: calm patches (a fixed noise over the land, nothing moves) on faces
// that look up. A cover of 0 leaves the colour untouched, 1 covers every such face.
vec3 snowed(vec3 color, vec3 world, float up) {
	float cover = nature_snow.a;
	if (cover <= 0.0) {
		return color;
	}
	vec2 p = world.xz / max(nature_snow_shape.x, 0.01);
	float n = 0.65 * nature_noise(p) + 0.35 * nature_noise(p * 3.1 + vec2(17.0, 5.0));
	float edge = nature_snow_shape.y;
	float limit = cover * (1.0 + 2.0 * edge) - edge;
	float lies = 1.0 - smoothstep(limit - edge, limit + edge + 0.0001, n);
	float facing = smoothstep(nature_snow_shape.z - 0.1, nature_snow_shape.z + 0.1, up);
	return mix(color, nature_snow.rgb, lies * facing);
}
