// Warm comic (B2501, reference D355): flat colour areas with soft light steps, warm shadows.
// Colour comes from the vertex colour (terrain, props) or, for figures, from a region index in
// COLOR.r that picks one of the region colours below (skin, top, bottom, shoes, hair).
shader_type spatial;
render_mode diffuse_lambert, specular_disabled, cull_back;

#include "res://shaders/warm_comic.gdshaderinc"
