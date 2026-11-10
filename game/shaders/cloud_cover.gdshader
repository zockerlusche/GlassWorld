// Cloud cover over what the player has not seen yet (B2-6 R25): a soft, light layer of clouds, never
// black. One quad fills the screen; for every pixel the place of the drawn world behind it is read from
// the depth of the picture, and the reveal map (one texel per cell of the island: 0 = unseen, 1 = seen,
// in between while a cell clears; CloudCover.cs, RevealMap) says how much cloud lies there. So ground,
// plants, objects and figures in unseen cells are hidden from every camera angle, and seen land is never
// covered. The edge is soft (blurred map) and puffy (billows push it in and out); the billows drift
// slowly (drift_offset, set per frame, so a pause holds them). The colour follows the day phase like the
// unlit water (global water_light), so the clouds do not glow at night. The sky stays open.
// Drawn after the sea and before the symbols above heads (render priority, CloudCover.cs).
// Presentation only.
shader_type spatial;
render_mode unshaded, blend_mix, cull_disabled, depth_draw_never, depth_test_disabled, shadows_disabled, fog_disabled;

uniform sampler2D depth_texture : hint_depth_texture, filter_nearest;
uniform sampler2D reveal_map : filter_linear, repeat_disable;
// World x and z of the map corner (cell 0, 0) and the size of the map in metres.
uniform vec2 map_origin = vec2(0.0, 0.0);
uniform vec2 map_size_m = vec2(192.0, 192.0);
// One cell in map coordinates (1 / cells).
uniform vec2 cell_uv = vec2(0.0052, 0.0052);
uniform vec4 light_color : source_color = vec4(0.96, 0.94, 0.9, 1.0);
uniform vec4 shade_color : source_color = vec4(0.89, 0.85, 0.82, 1.0);
uniform float puff_m = 26.0;
// World height of the flat layer the billow pattern lies on (island height + layer_height_m).
uniform float layer_y = 4.0;
uniform float softness_cells = 2.5;
uniform float edge_softness = 0.22;
uniform float edge_puff = 0.6;
uniform float band = 0.5;
uniform float band_softness = 0.16;
uniform float opacity = 1.0;
uniform float daylight_share = 0.85;
uniform vec2 drift_offset = vec2(0.0, 0.0);
// Tint of the day phase (DaylightLook, content/look/daylight.json water_light); white by default.
global uniform vec4 water_light;

// Exact integer hash of a lattice corner: two neighbouring cells always get the same value for a shared
// corner. (A sin hash differs there by rounding, which tore the billows into visible squares.)
float hash(vec2 p) {
	uvec2 q = uvec2(ivec2(p) + ivec2(32768, 32768));
	uint h = q.x * 1664525u + q.y * 22695477u + 1013904223u;
	h ^= h >> 16u;
	h *= 2246822519u;
	h ^= h >> 13u;
	h *= 3266489917u;
	h ^= h >> 16u;
	return float(h >> 8u) / 16777215.0;
}

float value_noise(vec2 p) {
	vec2 cell = floor(p);
	vec2 f = fract(p);
	vec2 u = f * f * (3.0 - 2.0 * f);
	float a = hash(cell);
	float b = hash(cell + vec2(1.0, 0.0));
	float c = hash(cell + vec2(0.0, 1.0));
	float d = hash(cell + vec2(1.0, 1.0));
	return mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
}

// Two sizes of soft billows, 0..1.
float billows(vec2 p) {
	return value_noise(p) * 0.6 + value_noise(p * 2.3 + vec2(17.0, 9.0)) * 0.4;
}

float seen_at(vec2 uv) {
	return textureLod(reveal_map, uv, 0.0).r;
}

void vertex() {
	// The quad (2 x 2) fills the screen, at the near end of the view.
	POSITION = vec4(VERTEX.xy, 1.0, 1.0);
}

void fragment() {
	float scene_depth = texture(depth_texture, SCREEN_UV).r;
	float cover = 0.0;
	vec2 place = vec2(0.0, 0.0);
	vec2 pattern_place = vec2(0.0, 0.0);
	// Where nothing is drawn (the sky, depth 0 at the far end) there is no cloud.
	if (scene_depth > 0.0) {
		vec4 view_pos = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, scene_depth, 1.0);
		view_pos.xyz /= view_pos.w;
		vec3 world_pos = (INV_VIEW_MATRIX * vec4(view_pos.xyz, 1.0)).xyz;
		place = world_pos.xz;
		// The billows lie on a flat layer, not on the drawn ground: where the view ray meets that layer.
		// So terrace steps, plants and figures under the cover never show through as breaks in the pattern.
		vec3 eye = CAMERA_POSITION_WORLD;
		vec3 ray = world_pos - eye;
		float plane_y = min(layer_y, eye.y - 1.0);
		pattern_place = place;
		if (ray.y < -0.0001) {
			pattern_place = eye.xz + ray.xz * min((plane_y - eye.y) / ray.y, 4096.0);
		}
		vec2 uv = (place - map_origin) / map_size_m;
		// Soft edge: the map blurred over softness_cells (weights sum to 1).
		vec2 o = cell_uv * softness_cells;
		float seen = seen_at(uv) * 0.36;
		seen += (seen_at(uv + vec2(o.x, 0.0)) + seen_at(uv - vec2(o.x, 0.0)) + seen_at(uv + vec2(0.0, o.y)) + seen_at(uv - vec2(0.0, o.y))) * 0.11;
		seen += (seen_at(uv + o * 0.7) + seen_at(uv - o * 0.7) + seen_at(uv + vec2(o.x, -o.y) * 0.7) + seen_at(uv + vec2(-o.x, o.y) * 0.7)) * 0.05;
		// Beyond the map there is no cloud; it fades out towards the map edge.
		vec2 rim = max(o * 2.0, vec2(0.0001, 0.0001));
		vec2 inside = smoothstep(vec2(0.0, 0.0), rim, uv) * smoothstep(vec2(0.0, 0.0), rim, vec2(1.0, 1.0) - uv);
		cover = (1.0 - clamp(seen, 0.0, 1.0)) * inside.x * inside.y;
	}

	vec2 p = (pattern_place + drift_offset) / max(puff_m, 0.01);
	float puff = billows(p);
	// Puffy edge: the billows move the edge only where the cover is neither full nor gone.
	float edge = cover + (puff - 0.5) * edge_puff * 4.0 * cover * (1.0 - cover);
	float soft = clamp(edge_softness, 0.01, 0.5);
	float alpha = smoothstep(0.5 - soft, 0.5 + soft, edge);

	// Two calm tones, lit and shaded, with a soft step between them.
	float tone_noise = billows(p * 0.5 + vec2(31.0, 7.0)) * 0.5 + puff * 0.5;
	float step_soft = max(band_softness, 0.001);
	float tone = smoothstep(band - step_soft, band + step_soft, tone_noise);
	vec3 color = mix(shade_color.rgb, light_color.rgb, tone);

	ALBEDO = color * mix(vec3(1.0, 1.0, 1.0), water_light.rgb, daylight_share);
	ALPHA = alpha * opacity;
}
