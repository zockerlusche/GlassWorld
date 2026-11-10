// Warm comic (B2501, reference D355), shared body of warm_comic.gdshader and figure_comic.gdshader:
// flat colour areas with soft light steps, warm shadows. Colour comes from the vertex colour (terrain,
// props) or, for figures, from a region index in COLOR.r that picks one of the region colours below
// (skin, top, bottom, shoes, hair). Materials of the land (ground, objects, grass) switch on the living
// nature (B2-5 R21/R24, nature.gdshaderinc): season colours, snow cover and sway, all from global parameters.

uniform vec4 albedo : source_color = vec4(1.0);
uniform bool use_regions = false;
uniform bool use_vertex_color = true;
uniform vec4 region_skin : source_color = vec4(0.91, 0.72, 0.58, 1.0);
uniform vec4 region_top : source_color = vec4(0.32, 0.48, 0.53, 1.0);
uniform vec4 region_bottom : source_color = vec4(0.35, 0.33, 0.39, 1.0);
uniform vec4 region_shoes : source_color = vec4(0.18, 0.15, 0.14, 1.0);
uniform vec4 region_hair : source_color = vec4(0.41, 0.15, 0.03, 1.0);
uniform vec4 region_eyes : source_color = vec4(0.12, 0.09, 0.08, 1.0);
// Which body parts show skin: short sleeves, shorts or a dress (upper legs in the top colour).
// Body regions clothing may cover (F4): 0 skin, 1 top colour, 2 bottom colour.
uniform int forearm_paint = 0;
uniform int thigh_paint = 2;
uniform int lower_thigh_paint = 2;
uniform int shin_paint = 2;

// Light ramp: brightness of the shadow, middle and lit step and where the steps start (palette.json ramp).
uniform float shade_dark = 0.78;
uniform float shade_mid = 0.9;
uniform float step_mid = 0.18;
uniform float step_lit = 0.5;
uniform float softness = 0.08;
uniform vec4 shadow_tint : source_color = vec4(0.86, 0.78, 0.86, 1.0);

// Living nature: off for figures and everything that is not part of the land.
uniform bool use_nature = false;
// How far the top of the model moves in full wind and the height of that top (model units); 0 = still.
uniform float sway_m = 0.0;
uniform float sway_height_m = 1.0;

#include "res://shaders/nature.gdshaderinc"

varying vec3 base_color;
varying vec3 nature_world;
varying float nature_up;
#ifdef FADE_ALPHA
varying float fade_alpha;
#endif

vec3 paint(int mode) {
	return mode == 1 ? region_top.rgb : (mode == 2 ? region_bottom.rgb : region_skin.rgb);
}

vec3 region_color(float r) {
	int i = int(round(r * 10.0));
	if (i == 1) return region_skin.rgb;
	if (i == 2) return region_top.rgb;
	if (i == 3) return region_bottom.rgb;
	if (i == 4) return region_shoes.rgb;
	if (i == 5) return region_hair.rgb;
	if (i == 6) return paint(shin_paint);
	if (i == 7) return paint(forearm_paint);
	if (i == 8) return paint(thigh_paint);
	if (i == 9) return region_eyes.rgb;
	if (i == 10) return paint(lower_thigh_paint);
	return region_top.rgb;
}

void vertex() {
	base_color = use_regions ? region_color(COLOR.r) : (use_vertex_color ? COLOR.rgb : vec3(1.0)) * albedo.rgb;
	nature_world = vec3(0.0);
	nature_up = 0.0;
	if (use_nature) {
		base_color = season_color(base_color, CUSTOM0);
		VERTEX = sway(VERTEX, MODEL_MATRIX, sway_m, sway_height_m);
		nature_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
		nature_up = normalize(MODEL_NORMAL_MATRIX * NORMAL).y;
	}
#ifdef FADE_ALPHA
	fade_alpha = COLOR.a;
#endif
}

void fragment() {
	ALBEDO = use_nature ? snowed(base_color, nature_world, nature_up) : base_color;
#ifdef FADE_ALPHA
	ALPHA = fade_alpha;
#endif
}

void light() {
	float ndl = dot(NORMAL, LIGHT);
	float lit = ndl * ATTENUATION;
	float mid = smoothstep(step_mid - softness, step_mid + softness, lit);
	float top = smoothstep(step_lit - softness, step_lit + softness, lit);
	float brightness = mix(shade_dark, mix(shade_mid, 1.0, top), mid);
	vec3 tint = mix(shadow_tint.rgb, vec3(1.0), mid);
	// Flat look: the light colour only scales the albedo by the step, no specular, no gradients.
	DIFFUSE_LIGHT += ALBEDO * brightness * tint * LIGHT_COLOR / PI;
}
