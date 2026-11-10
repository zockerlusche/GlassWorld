// Warm comic, see-through (W1689 crown fade): the same look, season colours, snow and sway for a tree
// crown that fades between the camera and a figure; the opacity is the alpha of the instance colour of
// its MultiMesh. It writes no depth (depth_draw_opaque), exactly as the see-through twin did before
// (StandardMaterial3D with alpha): with depth_draw_always the crown would cut rain, leaves and other faded
// crowns behind it and show its own inner faces by triangle order; a depth prepass discards evenly
// see-through pixels anyway. Whether the overlapping crown balls sort well enough is judged in the window.
shader_type spatial;
render_mode diffuse_lambert, specular_disabled, cull_back, blend_mix, depth_draw_opaque;

#define FADE_ALPHA
#include "res://shaders/warm_comic.gdshaderinc"
