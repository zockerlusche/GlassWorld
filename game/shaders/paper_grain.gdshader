// Optional full-screen paper grain over the 3D view (variant for the style comparison; D355 itself
// shows almost none). Static per pixel so it never flickers (B2466).
shader_type canvas_item;

uniform sampler2D screen_texture : hint_screen_texture, filter_linear;
uniform float strength = 0.035;
uniform float fibre_scale = 2.0;

float hash(vec2 p) {
	return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453);
}

void fragment() {
	vec3 color = texture(screen_texture, SCREEN_UV).rgb;
	vec2 px = FRAGCOORD.xy / fibre_scale;
	float grain = hash(floor(px)) - 0.5;
	float fibre = hash(vec2(floor(px.x * 0.25), floor(px.y))) - 0.5;
	color += (grain * 0.7 + fibre * 0.3) * strength;
	COLOR = vec4(color, 1.0);
}
