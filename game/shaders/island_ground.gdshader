// Island ground of the look prototype (A1436 F2, TL #18976): grass edge, grass lip and the water line are
// computed per pixel from the same shape functions as the terrain (IslandPiece.ShoreX/GrassX), anti-
// aliased with fwidth, so the edges are smooth curves instead of steps of the triangle grid. Near the
// stream the vertex colour is used (COLOR.a = 0 there). Lighting as in warm_comic.gdshader.
shader_type spatial;
render_mode diffuse_lambert, specular_disabled, cull_back;

uniform vec4 grass : source_color = vec4(0.67, 0.78, 0.4, 1.0);
uniform vec4 grass_edge : source_color = vec4(0.56, 0.7, 0.35, 1.0);
uniform vec4 sand : source_color = vec4(0.95, 0.86, 0.66, 1.0);
uniform vec4 sand_wet : source_color = vec4(0.91, 0.81, 0.6, 1.0);
uniform vec4 foam : source_color = vec4(0.84, 0.93, 0.9, 1.0);
uniform float lip_width = 0.28;

uniform float shade_dark = 0.82;
uniform float shade_mid = 0.92;
uniform float step_mid = 0.18;
uniform float step_lit = 0.5;
uniform float softness = 0.08;
uniform vec4 shadow_tint : source_color = vec4(0.86, 0.78, 0.86, 1.0);

varying vec3 world_pos;

float shore_x(float z) {
	return 7.5 + 3.2 * sin(0.13 * z + 0.5) + 1.1 * sin(0.33 * z);
}

float grass_x(float z) {
	float inlet = (z - 3.0) / 5.0;
	return shore_x(z) - (7.5 + 1.8 * sin(0.21 * z + 1.3)) - 3.5 * exp(-inlet * inlet);
}

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	float inside = grass_x(world_pos.z) - world_pos.x;
	float seaward = world_pos.x - shore_x(world_pos.z);
	float aa = max(fwidth(inside), 0.002);
	float grass_amount = smoothstep(-aa, aa, inside);
	float lip = grass_amount * (1.0 - smoothstep(lip_width - aa, lip_width + aa, inside));
	vec3 beach = mix(sand.rgb, sand_wet.rgb, smoothstep(-1.8, -0.2, seaward));
	vec3 ground = mix(beach, grass.rgb, grass_amount);
	ground = mix(ground, grass_edge.rgb, lip * 0.85);
	ground = mix(ground, foam.rgb, smoothstep(-aa, aa, seaward));
	ALBEDO = mix(COLOR.rgb, ground, COLOR.a);
}

void light() {
	float lit = dot(NORMAL, LIGHT) * ATTENUATION;
	float mid = smoothstep(step_mid - softness, step_mid + softness, lit);
	float top = smoothstep(step_lit - softness, step_lit + softness, lit);
	float brightness = mix(shade_dark, mix(shade_mid, 1.0, top), mid);
	vec3 tint = mix(shadow_tint.rgb, vec3(1.0), mid);
	DIFFUSE_LIGHT += ALBEDO * brightness * tint * LIGHT_COLOR / PI;
}
