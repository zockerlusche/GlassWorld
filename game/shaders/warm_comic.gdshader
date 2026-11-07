// Warm comic (B2501, reference D355): flat colour areas with soft light steps, warm shadows.
// Colour comes from the vertex colour (terrain, props) or, for figures, from a region index in
// COLOR.r that picks one of the region colours below (skin, top, bottom, shoes, hair).
shader_type spatial;
render_mode diffuse_lambert, specular_disabled, cull_back;

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
uniform bool forearm_skin = true;
uniform bool shin_skin = false;
uniform bool thigh_top = false;

// Light ramp: brightness of the shadow, middle and lit step and where the steps start (palette.json ramp).
uniform float shade_dark = 0.78;
uniform float shade_mid = 0.9;
uniform float step_mid = 0.18;
uniform float step_lit = 0.5;
uniform float softness = 0.08;
uniform vec4 shadow_tint : source_color = vec4(0.86, 0.78, 0.86, 1.0);

varying vec3 base_color;

vec3 region_color(float r) {
	int i = int(round(r * 10.0));
	if (i == 1) return region_skin.rgb;
	if (i == 2) return region_top.rgb;
	if (i == 3) return region_bottom.rgb;
	if (i == 4) return region_shoes.rgb;
	if (i == 5) return region_hair.rgb;
	if (i == 6) return shin_skin ? region_skin.rgb : region_bottom.rgb;
	if (i == 7) return forearm_skin ? region_skin.rgb : region_top.rgb;
	if (i == 8) return thigh_top ? region_top.rgb : region_bottom.rgb;
	if (i == 9) return region_eyes.rgb;
	return region_top.rgb;
}

void vertex() {
	base_color = use_regions ? region_color(COLOR.r) : (use_vertex_color ? COLOR.rgb : vec3(1.0)) * albedo.rgb;
}

void fragment() {
	ALBEDO = base_color;
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
