// The clock of the water (W1724, review GW3 #199), shared by every water shader instead of TIME: it is
// driven by the game (NatureLook, WaterClock), so it stands still while the game is paused, and it wraps
// at its period. No shader uses the seconds raw: a cycle is rounded to a whole number of rounds per
// period, and a pattern that scrolls repeats after the whole number of steps it scrolls per period, so
// nothing jumps and nothing is picked anew when the clock wraps - whatever the period is.

// x: seconds of the water clock, in [0, y); y: its period in seconds.
global uniform vec2 water_time;

// Share of the period that has passed, 0..1.
float water_progress() {
	return water_time.x / max(water_time.y, 1.0);
}

// Whole rounds per period of a cycle with about hz rounds per second: at least one for any rate above 0
// (so nothing stands still under a short period), none without a rate.
float water_rounds(float hz) {
	if (hz <= 0.0) {
		return 0.0;
	}
	return max(floor(hz * max(water_time.y, 1.0) + 0.5), 1.0);
}

// How far a cycle with about hz rounds per second has run in this period, in rounds: from 0 up to
// water_rounds(hz), where it starts again. For patterns that repeat after water_rounds(hz) steps.
float water_run(float hz) {
	return water_rounds(hz) * water_progress();
}

// Phase 0..1 of a cycle with about hz rounds per second; it runs on without a jump at the wrap.
float water_cycle(float hz) {
	return fract(water_run(hz));
}
