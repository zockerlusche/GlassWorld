// Warm comic for inhabitants (W1695): the warm comic look that also marks every pixel where a figure is
// the visible surface in the stencil buffer (value 1). The see-through silhouette (figure_silhouette)
// draws only where the stencil is not 1, so it shows behind trees, rocks and objects but never against
// another inhabitant or the figure's own arms and head.
shader_type spatial;
render_mode diffuse_lambert, specular_disabled, cull_back;
stencil_mode write, compare_always, 1;

#include "res://shaders/warm_comic.gdshaderinc"
