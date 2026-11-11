// Optional ink outline (inverted hull) for the style comparison; D355 itself has almost no outlines.
shader_type spatial;
render_mode unshaded, cull_front;

uniform vec4 outline_color : source_color = vec4(0.23, 0.16, 0.13, 1.0);
uniform float width = 0.012;
// The sway of the plant it outlines (B2-5 R21), the same values as its warm comic material; 0 = still.
uniform float sway_m = 0.0;
uniform float sway_height_m = 1.0;
// Where the mesh carries its own outline directions in CUSTOM2 (the island's objects, TL #20750): one
// common direction for all vertices at a place, so the hull of a flat-shaded mesh stays one closed skin
// instead of falling apart into faces that float off the body. Off = along the normal.
uniform bool use_hull_directions = false;

#include "res://shaders/nature.gdshaderinc"

void vertex() {
	vec3 out_along = use_hull_directions ? CUSTOM2.xyz : NORMAL;
	VERTEX = sway(VERTEX, MODEL_MATRIX, sway_m, sway_height_m) + out_along * width;
}

void fragment() {
	ALBEDO = outline_color.rgb;
}
