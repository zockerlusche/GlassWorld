// Warm comic, see-through (W1689 crown fade): the same look, season colours, snow and sway for a tree
// crown that fades between the camera and a figure; the opacity is the alpha of the instance colour of
// its MultiMesh.
shader_type spatial;
render_mode diffuse_lambert, specular_disabled, cull_back, blend_mix, depth_draw_opaque;

#define FADE_ALPHA
#include "res://shaders/warm_comic.gdshaderinc"
