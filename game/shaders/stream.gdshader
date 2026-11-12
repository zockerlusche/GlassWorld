// Stream in the D355 look: light blue ribbon with white dashes that drift downstream.
// UV.x runs along the stream (metres), UV.y across (0..1). The dashes move by the water clock
// (water_time.gdshaderinc), not by TIME: they stand still in a pause, they drift a whole number of
// dashes per period of the clock, and the pattern of a lane repeats after as many, so no dash is picked
// anew when the clock wraps. Presentation only.
shader_type spatial;
render_mode unshaded, cull_disabled;
// Water is no cover for the see-through silhouette (A1569): it marks its pixels 2, like the ground.
stencil_mode write, compare_always, 2;

#include "res://shaders/water_time.gdshaderinc"

uniform vec4 water_color : source_color = vec4(0.52, 0.8, 0.86, 1.0);
uniform vec4 edge_color : source_color = vec4(0.62, 0.86, 0.9, 1.0);
uniform vec4 dash_color : source_color = vec4(0.95, 0.98, 1.0, 1.0);
uniform float flow_speed = 0.6;
// Tint of the day phase (DaylightLook, content/look/daylight.json water_light): the water is unlit,
// so the night darkens it here; white by default.
global uniform vec4 water_light;

float hash(float x) {
	return fract(sin(x * 91.7) * 43758.5453);
}

void fragment() {
	float across = abs(UV.y - 0.5) * 2.0;
	vec3 color = mix(water_color.rgb, edge_color.rgb, smoothstep(0.65, 1.0, across));
	float lane = floor(UV.y * 4.0);
	// Dashes drifted per period of the clock (a whole number), and the repeat of the pattern.
	float steps = max(water_rounds(flow_speed * 0.8), 1.0);
	float along = UV.x * 0.8 - water_run(flow_speed * 0.8) + hash(lane) * 7.0;
	float seg = mod(floor(along), steps);
	float local = fract(along);
	float on = step(0.55, hash(seg + lane * 13.0));
	float dash = on * smoothstep(0.0, 0.08, local) * (1.0 - smoothstep(0.3, 0.38, local));
	float lane_center = 1.0 - smoothstep(0.0, 0.12, abs(fract(UV.y * 4.0) - 0.5) - 0.05);
	color = mix(color, dash_color.rgb, dash * lane_center * (1.0 - smoothstep(0.7, 0.9, across)));
	ALBEDO = color * water_light.rgb;
}
