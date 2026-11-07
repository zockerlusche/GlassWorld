// Optional ink outline (inverted hull) for the style comparison; D355 itself has almost no outlines.
shader_type spatial;
render_mode unshaded, cull_front;

uniform vec4 outline_color : source_color = vec4(0.23, 0.16, 0.13, 1.0);
uniform float width = 0.012;

void vertex() {
	VERTEX += NORMAL * width;
}

void fragment() {
	ALBEDO = outline_color.rgb;
}
