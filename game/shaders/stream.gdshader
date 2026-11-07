// Stream in the D355 look: light blue ribbon with white dashes that drift downstream.
// UV.x runs along the stream (metres), UV.y across (0..1). Presentation only.
shader_type spatial;
render_mode unshaded, cull_disabled;

uniform vec4 water_color : source_color = vec4(0.52, 0.8, 0.86, 1.0);
uniform vec4 edge_color : source_color = vec4(0.62, 0.86, 0.9, 1.0);
uniform vec4 dash_color : source_color = vec4(0.95, 0.98, 1.0, 1.0);
uniform float flow_speed = 0.6;

float hash(float x) {
	return fract(sin(x * 91.7) * 43758.5453);
}

void fragment() {
	float across = abs(UV.y - 0.5) * 2.0;
	vec3 color = mix(water_color.rgb, edge_color.rgb, smoothstep(0.65, 1.0, across));
	float along = UV.x - TIME * flow_speed;
	float lane = floor(UV.y * 4.0);
	float seg = floor(along * 0.8 + hash(lane) * 7.0);
	float local = fract(along * 0.8 + hash(lane) * 7.0);
	float on = step(0.55, hash(seg + lane * 13.0));
	float dash = on * smoothstep(0.0, 0.08, local) * smoothstep(0.38, 0.3, local);
	float lane_center = smoothstep(0.12, 0.0, abs(fract(UV.y * 4.0) - 0.5) - 0.05);
	color = mix(color, dash_color.rgb, dash * lane_center * (1.0 - smoothstep(0.7, 0.9, across)));
	ALBEDO = color;
}
