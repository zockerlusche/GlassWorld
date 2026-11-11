// The inside of a rock niche (TL #20750): its own dark colour from the vertex and nothing else - no sun,
// no shade steps, no snow - so the opening reads as a hole in the rock in every light and from every
// side. The faces look into the hollow; seen from outside they are culled. Presentation only.
shader_type spatial;
render_mode unshaded, cull_back;

void fragment() {
	ALBEDO = COLOR.rgb;
}
