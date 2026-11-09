// See-through silhouette of an inhabitant (W1689): the last pass of the figure material. It is drawn only
// where something lies in front of the figure (inverted depth test), as a calm see-through shape in the
// inhabitant's colour, stronger at its edge. The surface is pulled towards the camera by min_cover_m first,
// so the figure's own clothes and arms never count as cover - only trees, rocks and objects do.
shader_type spatial;
render_mode unshaded, cull_back, depth_draw_never, depth_test_inverted, blend_mix, shadows_disabled, fog_disabled, skip_vertex_transform;

uniform vec4 color : source_color = vec4(1.0);
uniform float fill_alpha = 0.22;
uniform float edge_alpha = 0.75;
uniform float min_cover_m = 0.3;

void vertex() {
	VERTEX = (MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
	NORMAL = normalize((MODELVIEW_MATRIX * vec4(NORMAL, 0.0)).xyz);
	VERTEX -= normalize(VERTEX) * min_cover_m;
}

void fragment() {
	float rim = 1.0 - clamp(dot(NORMAL, VIEW), 0.0, 1.0);
	ALBEDO = color.rgb;
	ALPHA = mix(fill_alpha, edge_alpha, rim * rim);
}
