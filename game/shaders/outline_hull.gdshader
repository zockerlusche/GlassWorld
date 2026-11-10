// Optional ink outline (inverted hull) for the style comparison; D355 itself has almost no outlines.
shader_type spatial;
render_mode unshaded, cull_front;

uniform vec4 outline_color : source_color = vec4(0.23, 0.16, 0.13, 1.0);
uniform float width = 0.012;
// The sway of the plant it outlines (B2-5 R21), the same values as its warm comic material; 0 = still.
uniform float sway_m = 0.0;
uniform float sway_height_m = 1.0;

#include "res://shaders/nature.gdshaderinc"

void vertex() {
	VERTEX = sway(VERTEX, MODEL_MATRIX, sway_m, sway_height_m) + NORMAL * width;
}

void fragment() {
	ALBEDO = outline_color.rgb;
}
