// Wave crests on the sea and on lakes (W1724, content/look/terrain.json waves), shared by sea.gdshader
// and island_water.gdshader: light bowed strokes that travel with the wind. How many there are comes from
// the weather (global water_waves, set by NatureLook from content/look/weather.json): 0 = none, about
// 0.3 = a few small ones, 1 = many large ones.
// Each layer is a square grid that is fixed in the world and never turns (review GW3 #199). The wind only
// pushes the grid along: the game sums up direction times speed times time into a shift per layer
// (global water_wave_shift, WaveDrift) - so a turning wind bends the path of the crests but never slides
// or reshuffles them - and every stroke turns in its own place to lie across the eased wind direction.
// The amount only decides how many places carry a crest and how thick it is, so a change of weather lets
// crests swell and fade but never moves one. The pattern repeats after WAVE_PERIOD places and the shift
// wraps after as many, so that wrap does not show. Everything is smooth in time: nothing flickers (B2466).

#include "res://shaders/water_time.gdshaderinc"

// x: amount of waves 0..1, yz: eased unit direction the wind blows to on the ground (x, z).
global uniform vec4 water_waves;
// Shift of the crest grids in metres (x, z): xy of the small layer, zw of the large layer.
global uniform vec4 water_wave_shift;

// Places after which the crest pattern repeats; the same number as WaveDrift.PeriodCells.
const float WAVE_PERIOD = 64.0;

// Per layer: x size of one place of the grid (m), y longest crest (m), z thickness of a crest (m).
uniform vec3 wave_small = vec3(3.2, 2.2, 0.12);
uniform vec3 wave_large = vec3(8.0, 5.5, 0.3);
// Per layer: x share of the places that carry a crest when the layer is full, y amount at which the
// first crests appear, z amount from which the layer is full.
uniform vec3 wave_small_range = vec3(0.55, 0.05, 0.45);
uniform vec3 wave_large_range = vec3(0.6, 0.5, 1.0);

float wave_hash(vec2 p) {
	vec3 q = fract(vec3(p.x, p.y, p.x) * 0.1031);
	q += dot(q, q.yzx + 33.33);
	return fract((q.x + q.y) * q.z);
}

// The eased wind direction as a unit vector; a missing direction (no length) counts as +z.
vec2 wave_direction() {
	vec2 dir = water_waves.yz;
	float len = length(dir);
	if (len < 0.001) {
		return vec2(0.0, 1.0);
	}
	return dir / len;
}

// One layer: 0..1 where a crest lies at the ground position xz (metres).
float wave_layer(vec2 xz, vec2 shift, float amount, vec3 shape, vec3 range, float seed) {
	float full = clamp((amount - range.y) / max(range.z - range.y, 0.001), 0.0, 1.0);
	if (full <= 0.0) {
		return 0.0;
	}
	float cell_m = max(shape.x, 0.01);
	vec2 p = (xz - shift) / cell_m;
	vec2 id = mod(floor(p), WAVE_PERIOD) + vec2(seed);
	vec2 local = fract(p) - vec2(0.5);
	float pick = wave_hash(id);
	// How many places carry a crest: the threshold falls as the layer fills; soft, so a crest swells in.
	float limit = 1.0 - range.x * full;
	float present = smoothstep(limit, limit + 0.12, pick);
	// The stroke lies a little off the middle of its place and turns there to lie across the wind. Its
	// half length (0.34 at most), its bow and its thickness (0.08 at most) keep it inside the place
	// whichever way it is turned.
	vec2 centre = (vec2(wave_hash(id + vec2(5.3)), wave_hash(id + vec2(9.7))) - vec2(0.5)) * 0.16;
	vec2 dir = wave_direction();
	vec2 q = local - centre;
	float fa = dot(q, dir);
	float fc = dot(q, vec2(-dir.y, dir.x));
	float half_len = max(min(shape.y / cell_m * 0.5, 0.34) * (0.65 + 0.35 * wave_hash(id + vec2(3.1))), 0.01);
	float thick = max(min(shape.z / cell_m, 0.08) * (0.6 + 0.4 * full), 0.001);
	float bow = fa + 0.5 * fc * fc;
	float crest_line = 1.0 - smoothstep(thick * 0.5, thick, abs(bow));
	float ends = 1.0 - smoothstep(half_len * 0.55, half_len, abs(fc));
	// Each crest swells and fades slowly on its own.
	float life = 0.5 + 0.5 * sin(6.2831853 * water_cycle(0.07) + pick * 43.0);
	return crest_line * ends * present * smoothstep(0.1, 0.6, life);
}

// The crests of both layers at a ground position, 0..1. share scales the weather's amount (a lake is
// calmer than the sea).
float wave_crests(vec2 xz, float share) {
	float amount = clamp(water_waves.x * share, 0.0, 1.0);
	if (amount <= 0.0) {
		return 0.0;
	}
	float crest_a = wave_layer(xz, water_wave_shift.xy, amount, wave_small, wave_small_range, 0.0);
	float crest_b = wave_layer(xz, water_wave_shift.zw, amount, wave_large, wave_large_range, 17.0);
	return max(crest_a * 0.8, crest_b);
}
