// Island sky (B2-5, A1367): gradient from the horizon colour to the top colour, a darker band below
// the horizon so the sea line stays readable, procedural stars and a soft moon disc. All values come
// from content/look/daylight.json (DaylightLook); no time-based change, so the radiance stays cached.
shader_type sky;

uniform vec3 top_color : source_color = vec3(0.48, 0.72, 0.91);
uniform vec3 horizon_color : source_color = vec3(0.81, 0.9, 0.95);
uniform float horizon_falloff = 0.45;
uniform float below_horizon = 0.55;
uniform float stars = 0.0;
uniform float star_density = 0.035;
uniform float star_brightness = 1.4;
uniform float star_horizon_fade = 0.25;
uniform vec3 moon_direction = vec3(0.0, 0.6, -0.8);
uniform vec3 moon_color : source_color = vec3(0.93, 0.95, 0.97);
uniform float moon_size_deg = 1.6;
uniform float moon_glow = 0.35;
uniform float moon = 0.0;

float hash(vec3 p) {
	p = fract(p * vec3(0.1031, 0.1030, 0.0973));
	p += dot(p, p.yxz + 33.33);
	return fract((p.x + p.y) * p.z);
}

void sky() {
	vec3 dir = normalize(EYEDIR);
	float up = dir.y;
	vec3 color = mix(horizon_color, top_color, pow(clamp(up, 0.0, 1.0), horizon_falloff));
	// Below the horizon: a calm, darker version of the horizon colour, so sea and sky part visibly.
	color = mix(color, horizon_color * below_horizon, 1.0 - smoothstep(-0.04, 0.0, up));

	if (stars > 0.0 && up > 0.0) {
		// One possible star per cell of a 3D grid around the eye; the hash picks the cells and the spot.
		vec3 p = dir * 60.0;
		vec3 cell = floor(p);
		float pick = hash(cell);
		if (pick < star_density) {
			// The spot is pulled onto the sphere the eye ray runs on, so every picked star is seen.
			vec3 spot = normalize(cell + 0.25 + 0.5 * vec3(hash(cell + 11.0), hash(cell + 23.0), hash(cell + 37.0))) * 60.0;
			float d = length(p - spot);
			float size = 0.08 + 0.1 * hash(cell + 51.0);
			float star = (1.0 - smoothstep(0.0, size, d)) * (0.4 + 0.6 * hash(cell + 71.0));
			float fade = star_horizon_fade > 0.0 ? smoothstep(0.0, star_horizon_fade, up) : 1.0;
			color += vec3(star * star_brightness * stars * fade);
		}
	}

	if (moon > 0.0) {
		float angle = degrees(acos(clamp(dot(dir, normalize(moon_direction)), -1.0, 1.0)));
		float disc = 1.0 - smoothstep(moon_size_deg * 0.8, moon_size_deg, angle);
		float halo = moon_glow * exp(-angle / (moon_size_deg * 3.0));
		color = mix(color, moon_color, disc * moon);
		color += moon_color * halo * moon * (1.0 - disc);
	}

	COLOR = color;
}
