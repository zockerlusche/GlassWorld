// The inside of a rock niche (TL #20750): its own dark colour from the vertex and nothing else - no sun,
// no shade steps, no snow - so the opening reads as a hole in the rock in every light and from every
// side. The faces look into the hollow; seen from outside they are culled. Presentation only.
// It is rock like the outside: it marks its pixels 3 in the stencil buffer, so a figure behind a niche's side wall
// shows its see-through silhouette (A1712, D1112 #1; the scheme is in figure_silhouette).
shader_type spatial;
render_mode unshaded, cull_back;
stencil_mode write, compare_always, 3;

void fragment() {
	ALBEDO = COLOR.rgb;
}
