# Departure-timing levers for a Mun return to Kerbin (patched conics, stock constants). #482 spike.
import math
MU_K = 3.5316e12; R_K = 600e3; P_ROT = 21549.425
MU_M = 6.5138398e10; R_M = 200e3; A_M = 12e6; P_M = 2 * math.pi * math.sqrt(A_M**3 / MU_K)
wk = 360 / P_ROT; wm = 360 / P_M
km_per_deg = R_K / 1000 * math.pi / 180
print("Mun period %.0f s; Kerbin sidereal day %.1f s" % (P_M, P_ROT))
d = wm - wk
print("landing drift per hour of departure delay: %.2f deg = %.0f km at the equator" % (d * 3600, d * 3600 * km_per_deg))
print("departure time for 50 km: %.1f s" % (50 / km_per_deg / abs(d)))
print("landing longitude repeats every %.0f s (%.2f h)" % (360 / abs(d), 360 / abs(d) / 3600))
for alt in (15e3, 35e3, 38e3, 100e3):
    r = R_M + alt; T = 2 * math.pi * math.sqrt(r**3 / MU_M)
    print("parking orbit %3.0f km: period %5.0f s, landing step per orbit %6.1f deg (%5.0f km)" % (alt / 1e3, T, d * T, d * T * km_per_deg))
# From a 38 km parking orbit: worst miss over all target longitudes when the craft may wait up to N orbits.
r = R_M + 38e3; T = 2 * math.pi * math.sqrt(r**3 / MU_M); step = d * T
for N in (1, 3, 9, 18, 36, 72):
    offs = sorted(((k * step) % 360) for k in range(N))
    gaps = [((offs[(i + 1) % N] - offs[i]) % 360) or 360 for i in range(N)]
    print("wait up to %2d orbits (%5.1f h): worst miss %6.1f deg (%5.0f km)" % (N, N * T / 3600, max(gaps) / 2, max(gaps) / 2 * km_per_deg))

# Time-of-flight lever: from the Mun's distance to a 35 km periapsis, with an inward radial speed (a faster return).
rp = R_K + 35e3; ra = A_M; vmun = math.sqrt(MU_K / A_M)

def tof_from(vr):
    lo, hi = 0.0, vmun
    for _ in range(200):
        vt = (lo + hi) / 2
        v2 = vt * vt + vr * vr; eps = v2 / 2 - MU_K / ra; h = ra * vt
        e = math.sqrt(max(0, 1 + 2 * eps * h * h / MU_K**2)); p = h * h / MU_K; peR = p / (1 + e)
        if peR < rp: lo = vt
        else: hi = vt
    a = -MU_K / (2 * eps); n = math.sqrt(MU_K / a**3)
    nu0 = math.acos(max(-1, min(1, (p / ra - 1) / e)))
    E0 = 2 * math.atan(math.sqrt((1 - e) / (1 + e)) * math.tan(nu0 / 2))
    t = (E0 - e * math.sin(E0)) / n
    vinf = math.sqrt((vmun - vt)**2 + vr**2)
    return t, vt, vinf, math.degrees(nu0)

r0 = R_M + 38e3; vc = math.sqrt(MU_M / r0)
base = None
for vr in (0, 50, 100, 150, 200, 300):
    t, vt, vinf, nu = tof_from(vr)
    ej = math.sqrt(vinf**2 + 2 * MU_M / r0) - vc
    if base is None: base = (t, ej, nu)
    # Arrival later by dt turns the ground under it west by wk*dt; a smaller swept angle puts periapsis
    # that much nearer the departure direction, i.e. further west in inertial terms.
    dland = -wk * (t - base[0]) + (nu - base[2])
    print("radial %3d m/s: tof %6.0f s (%+5.0f s) ejection %5.1f m/s (%+5.1f) swept %6.1f deg landing shift %+6.1f deg (%+5.0f km)" % (
        vr, t, t - base[0], ej, ej - base[1], nu, dland, dland * km_per_deg))
