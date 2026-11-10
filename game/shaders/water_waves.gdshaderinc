// Wave crests on the sea and on lakes (W1724, content/look/terrain.json waves), shared by sea.gdshader
// and island_water.gdshader: light bowed lines that travel with the wind. How many there are comes from
// the weather (global water_waves, set by NatureLook from content/look/weather.json): 0 = none, about
// 0.3 = a few small ones, 1 = many large ones. Each layer is a fixed grid that travels with the wind; the
// amount only decides how many of its places carry a crest and how thick it is, so a change of weather
// lets crests swell and fade but never moves one. Everything is smooth in time: nothing flickers (B2466).

// x: amount of waves 0..1, yz: unit direction the wind blows to on the ground (x, z).
global uniform vec4 water_waves;

// Per layer: x distance of the rows along the wind (m), y room of one crest across the wind (m),
// z thickness of a crest (m), w speed along the wind (m/s).
uniform vec4 wave_small = vec4(2.6, 3.4, 0.12, 0.45);
uniform vec4 wave_large = vec4(6.0, 9.0, 0.3, 0.9);
// Per layer: x share of the places that carry a crest when the layer is full, y amount at which the
// first crests appear, z amount from which the layer is full.
uniform vec3 wave_small_range = vec3(0.55, 0.05, 0.45);
uniform vec3 wave_large_range = vec3(0.6, 0.5, 1.0);

float wave_hash(vec2 p) {
	vec3 q = fract(vec3(p.x, p.y, p.x) * 0.1031);
	q += dot(q, q.yzx + 33.33);
	return fract((q.x + q.y) * q.z);
}

// One layer: 0..1 where a crest lies at the ground position xz (metres).
float wave_layer(vec2 xz, float time, float amount, vec4 shape, vec3 range, float seed) {
	float full = clamp((amount - range.y) / max(range.z - range.y, 0.001), 0.0, 1.0);
	if (full <= 0.0) {
		return 0.0;
	}
	vec2 dir = water_waves.yz;
	float spacing = max(shape.x, 0.01);
	float along = (dot(xz, dir) - time * shape.w) / spacing;
	float row = floor(along);
	float fa = fract(along) - 0.5;
	// Every row is shifted sideways by its own amount, so the crests do not stand in columns.
	float across = dot(xz, vec2(-dir.y, dir.x)) / max(shape.y, 0.01) + wave_hash(vec2(row, seed)) * 7.0;
	float column = floor(across);
	float fc = fract(across) - 0.5;
	float pick = wave_hash(vec2(row, column) + vec2(seed));
	// How many places carry a crest: the threshold falls as the layer fills; soft, so a crest swells in.
	float limit = 1.0 - range.x * full;
	float present = smoothstep(limit, limit + 0.12, pick);
	// The crest: a bowed line, thin along the wind, long across it, fading out at its ends.
	float half_len = 0.25 + 0.2 * wave_hash(vec2(column, row) + vec2(seed + 3.1));
	float thick = max(min(shape.z / spacing, 0.2) * (0.6 + 0.4 * full), 0.001);
	float bow = fa + 0.6 * fc * fc - 0.04;
	float crest_line = 1.0 - smoothstep(thick * 0.5, thick, abs(bow));
	float ends = 1.0 - smoothstep(half_len * 0.55, half_len, abs(fc));
	// Each crest swells and fades slowly on its own.
	float life = 0.5 + 0.5 * sin(time * 0.45 + pick * 43.0);
	return crest_line * ends * present * smoothstep(0.1, 0.6, life);
}

// The crests of both layers at a ground position, 0..1. share scales the weather's amount (a lake is
// calmer than the sea).
float wave_crests(vec2 xz, float time, float share) {
	float amount = clamp(water_waves.x * share, 0.0, 1.0);
	if (amount <= 0.0) {
		return 0.0;
	}
	float crest_a = wave_layer(xz, time, amount, wave_small, wave_small_range, 0.0);
	float crest_b = wave_layer(xz, time, amount, wave_large, wave_large_range, 17.0);
	return max(crest_a * 0.8, crest_b);
}
