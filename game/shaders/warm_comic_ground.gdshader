// Warm comic for the island's ground (A1569, D843 a): the same look as warm_comic, but the ground marks its
// pixels 2 in the stencil buffer - it is no cover, so a figure behind a ridge shows no silhouette through it.
shader_type spatial;
render_mode diffuse_lambert, specular_disabled, cull_back;
stencil_mode write, compare_always, 2;

#include "res://shaders/warm_comic.gdshaderinc"
