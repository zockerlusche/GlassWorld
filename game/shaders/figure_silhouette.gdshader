// See-through silhouette of an inhabitant (W1689): the last pass of the figure material. It is drawn only
// where something lies in front of the figure (inverted depth test), as a calm see-through shape in the
// inhabitant's colour, stronger at its edge. Only trees, rocks and objects count as cover (W1695): every
// opaque surface marks its pixels in the stencil buffer - figures 1 (figure_comic), ground and water 2
// (warm_comic_ground, sea, stream, island_water), cover 3 (warm_comic) - and the front-most surface's value
// stays, whatever the drawing order. This pass draws only where it is 3: never through the ground or the
// water (A1569, D843 a), never against another inhabitant or through the figure's own arms and head.
// Shaders without a stencil line write nothing (ink hulls, faded crowns, the cave inside, clouds, the look
// prototype's island_ground): with compare_equal 3 they can only hide an outline, never add one.
// The surface is pulled towards the camera by min_cover_m first, so cover right at the body does not count.
shader_type spatial;
render_mode unshaded, cull_back, depth_draw_never, depth_test_inverted, blend_mix, shadows_disabled, fog_disabled, skip_vertex_transform;
stencil_mode read, compare_equal, 3;

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
