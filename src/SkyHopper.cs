using System.Collections.Generic;
using UnityEngine;

// SKY HOPPER — a one-touch 3D tower-climber (Icy Tower x Doodle Jump blend).
// A chunky jelly hero AUTO-BOUNCES up an endless sky-tower; the ONLY control is STEER left/right
// (arrows / A,D / hold-drag pointer & touch). Land on a platform and you instantly spring up again.
// Icy-Tower flavour: the faster you're moving sideways when you land, the HIGHER you leap, so building
// horizontal speed (and wall-bouncing off the shaft's glass walls) lets you skip several floors at once
// for chained COMBO bonuses. Grab floating coins. Springs fling you sky-high. The camera only ever rises
// and a soft "fall line" creeps up from below — drop beneath it and it's game over. 30 seconds in you're
// already wall-bouncing for combos and racing the rising floor.
//
// Built entirely in code (CreatePrimitive + a couple procedural meshes) so it renders reliably in WebGL
// with engine-code stripping disabled. NO Rigidbody/colliders anywhere: the hero is pure Transform-driven
// (hand-integrated arcade physics) and every contact is a distance/projection test. Coexists with the
// permanent Juice (sfx/bgm/particles) & AutoShot helpers.
public class SkyHopper : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        Application.runInBackground = true;
        var go = new GameObject("__SkyHopper");
        go.AddComponent<SkyHopper>();
        DontDestroyOnLoad(go);
    }

    // ---------------------------------------------------------------- tuning
    const float WALL = 6.0f;          // shaft half-width (x of the glass walls)
    const float G = 38f;              // gravity (units/s^2)
    const float JUMP_BASE = 17.2f;    // auto-bounce vy with zero horizontal speed (apex ~3.85 > row gap)
    const float JUMP_MOM = 0.46f;     // extra vy per unit of |vx| (Icy-Tower momentum: fast = higher)
    const float MAX_VX = 15f;         // horizontal speed cap
    const float ACCEL_X = 70f;        // steering acceleration
    const float FRICT_X = 14f;        // horizontal damping when not steering
    const float WALL_BOUNCE = 0.74f;  // velocity kept on a wall bounce
    const float SPRING_VY = 31f;      // spring launch
    const float P_RAD = 0.7f;         // hero collision radius (feet offset)
    const float ROW_MIN = 2.55f, ROW_MAX = 3.55f; // vertical spacing range (max < base apex => always reachable)
    const int POOL = 36;              // recycled platform count
    const float BASE_DIST = 13.0f;    // close, juicy camera distance on wide screens
    const float TARGET_HALF_W = WALL + 0.6f; // horizontal world half-width the camera must always show
    const float METER = 1f;           // world units per displayed metre

    // ---------------------------------------------------------------- scene refs
    Transform camT; Camera camComp;
    Transform heroT, heroVisual; // root carries position; visual carries squash/stretch
    Transform fallLine;          // glowing danger band that creeps up from below
    TextMesh hudHeight, hudScore, hudBest, comboText, bannerText, dbg;
    Material wallMat;

    // ---------------------------------------------------------------- hero state
    enum State { Playing, Dead }
    State state = State.Playing;
    float px, py, vx, vy;        // hero centre position + velocity
    float prevFeet;              // feet height last frame (for swept landing test)
    float startY;                // baseline for height score
    float maxPy;                 // highest centre reached this run (height score)
    float squash = 1f;           // 1 = neutral; <1 squashed, >1 stretched
    float spin;                  // cosmetic yaw of the jelly
    int facing = 1;
    bool attract = true;         // auto-demo until first input
    bool showDbg;
    float airT;                  // time since last landing (for combo grace)

    // ---------------------------------------------------------------- scoring
    int score, best, coins, combo, bestCombo;
    float comboTimer;            // window to keep a combo alive
    float lastLandY;             // y of the platform last landed on
    float comboFlash;

    // ---------------------------------------------------------------- camera / floor
    float camFollowY;            // smoothed focus height (never decreases)
    float fallY;                 // world y of the kill line
    float autoRise;              // how fast the kill line creeps (ramps with height)
    float camX, fovPunch;
    float deathBelow = 9f;       // dynamic: how far the kill line can sit below the focus (set by camera)
    bool started;                // becomes true on first jump/input (delays the fall line)

    // ---------------------------------------------------------------- platforms
    enum PType { Normal, Moving, Spring, Break }
    class Plat
    {
        public Transform t;          // root
        public Transform deck;       // the slab (for colour swaps / break tilt)
        public Transform spring;     // optional spring nub
        public Transform coin;       // optional coin
        public float x, y, halfW;
        public PType type;
        public float baseX, amp, mvSpeed, phase; // moving
        public bool breaking; public float breakT;
        public bool coinAlive;
    }
    readonly List<Plat> plats = new List<Plat>();
    float genY, genX;            // generator cursor (top of the spawned stack)
    float runTime;

    // ---------------------------------------------------------------- HUD layout (aspect-adaptive)
    float hudScale = 1f, halfH = 2.7f, halfW = 4.6f;
    const float HUD_Z = 6.5f;

    Material platMatA, platMatB, springMat, coinMat, heroMat, eyeMat, pupilMat, fallMat, cloudMat;

    // ===================================================================== boot
    void Start()
    {
        foreach (var c in FindObjectsByType<Camera>(FindObjectsSortMode.None)) Destroy(c.gameObject);
        foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None)) Destroy(l.gameObject);

        best = PlayerPrefs.GetInt("skyhopper_best", 0);
        bestCombo = PlayerPrefs.GetInt("skyhopper_bestcombo", 0);

        BuildEnvironment();
        BuildCamera();
        BuildHero();
        BuildPlatformPool();
        BuildFallLine();
        BuildHud();

        ResetRun();
    }

    // ===================================================================== materials
    static Material Mat(Color c, float metallic = 0f, float smooth = 0.2f, bool emissive = false, float alpha = 1f)
    {
        var sh = Shader.Find("Universal Render Pipeline/Lit");
        if (sh == null) sh = Shader.Find("Standard");
        var m = new Material(sh);
        c.a = alpha;
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metallic);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smooth);
        if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", smooth);
        if (emissive && m.HasProperty("_EmissionColor"))
        {
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", c * 0.75f);
        }
        if (alpha < 1f) SetTransparent(m, c);
        return m;
    }

    static void SetTransparent(Material m, Color c)
    {
        // URP/Lit transparent surface setup
        if (m.HasProperty("_Surface")) m.SetFloat("_Surface", 1f);
        m.SetFloat("_Blend", 0f);
        if (m.HasProperty("_SrcBlend")) m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        if (m.HasProperty("_DstBlend")) m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        if (m.HasProperty("_ZWrite")) m.SetInt("_ZWrite", 0);
        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        m.DisableKeyword("_ALPHATEST_ON");
        m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
    }

    static GameObject Prim(PrimitiveType pt, Transform parent, Vector3 lpos, Vector3 lscale, Material shared)
    {
        var g = GameObject.CreatePrimitive(pt);
        var col = g.GetComponent<Collider>(); if (col != null) Destroy(col);
        g.transform.SetParent(parent, false);
        g.transform.localPosition = lpos;
        g.transform.localScale = lscale;
        g.GetComponent<Renderer>().sharedMaterial = shared;
        return g;
    }

    // ===================================================================== environment
    void BuildEnvironment()
    {
        platMatA = Mat(new Color(0.35f, 0.78f, 0.95f), 0.1f, 0.55f);
        platMatB = Mat(new Color(0.55f, 0.92f, 0.62f), 0.1f, 0.55f);
        springMat = Mat(new Color(1f, 0.83f, 0.2f), 0.3f, 0.7f, true);
        coinMat = Mat(new Color(1f, 0.86f, 0.22f), 0.5f, 0.8f, true);
        heroMat = Mat(new Color(1f, 0.42f, 0.62f), 0.05f, 0.75f);
        eyeMat = Mat(Color.white, 0f, 0.7f);
        pupilMat = Mat(new Color(0.08f, 0.08f, 0.12f), 0f, 0.3f);
        fallMat = Mat(new Color(1f, 0.32f, 0.28f), 0f, 0.4f, true, 0.5f);
        cloudMat = Mat(new Color(0.95f, 0.97f, 1f), 0f, 0.1f);
        wallMat = Mat(new Color(0.62f, 0.78f, 0.95f), 0.2f, 0.85f, false, 0.16f);

        var sun = new GameObject("Sun").AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.color = new Color(1f, 0.97f, 0.9f);
        sun.intensity = 1.25f;
        sun.transform.rotation = Quaternion.Euler(48f, 28f, 0f);
        sun.shadows = LightShadows.Soft;

        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = new Color(0.62f, 0.78f, 0.98f);
        RenderSettings.ambientEquatorColor = new Color(0.58f, 0.66f, 0.82f);
        RenderSettings.ambientGroundColor = new Color(0.40f, 0.46f, 0.58f);

        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Linear;
        RenderSettings.fogColor = new Color(0.70f, 0.83f, 0.98f);
        RenderSettings.fogStartDistance = 55f;
        RenderSettings.fogEndDistance = 150f;

        // back wall of the shaft (tall panel) + two glass side walls
        var back = Prim(PrimitiveType.Cube, null, new Vector3(0, 0, 3.2f), new Vector3(WALL * 2f + 2f, 4000f, 0.5f),
            Mat(new Color(0.30f, 0.42f, 0.62f), 0.1f, 0.3f));
        back.transform.position = new Vector3(0, 0, 3.2f);
        // vertical pillar stripes on the back wall for parallax/height read
        var stripeMat = Mat(new Color(0.24f, 0.34f, 0.52f), 0.1f, 0.3f);
        for (int i = -3; i <= 3; i++)
            Prim(PrimitiveType.Cube, null, new Vector3(i * 2.0f, 0, 3.0f), new Vector3(0.35f, 4000f, 0.2f), stripeMat)
                .transform.position = new Vector3(i * 2.0f, 0, 3.0f);

        for (int s = -1; s <= 1; s += 2)
        {
            var w = Prim(PrimitiveType.Cube, null, Vector3.zero, new Vector3(0.4f, 4000f, 6.2f), wallMat);
            w.transform.position = new Vector3(s * (WALL + 0.2f), 0, 0.6f);
            // bright rail caps so the bounce walls read clearly
            var rail = Prim(PrimitiveType.Cube, null, Vector3.zero, new Vector3(0.55f, 4000f, 0.5f),
                Mat(new Color(0.95f, 0.97f, 1f), 0.3f, 0.9f, true));
            rail.transform.position = new Vector3(s * (WALL + 0.2f), 0, -2.3f);
        }
    }

    void BuildCamera()
    {
        var cgo = new GameObject("MainCamera");
        cgo.tag = "MainCamera";
        camComp = cgo.AddComponent<Camera>();
        camComp.clearFlags = CameraClearFlags.SolidColor;
        camComp.backgroundColor = new Color(0.55f, 0.74f, 0.97f);
        camComp.fieldOfView = 60f;
        camComp.farClipPlane = 400f;
        cgo.AddComponent<AudioListener>();
        camT = cgo.transform;
    }

    // ===================================================================== hero (low-poly jelly)
    void BuildHero()
    {
        heroT = new GameObject("Hero").transform;
        heroVisual = new GameObject("HeroVisual").transform;
        heroVisual.SetParent(heroT, false);

        Prim(PrimitiveType.Sphere, heroVisual, new Vector3(0, 0, 0), new Vector3(1.3f, 1.15f, 1.2f), heroMat); // body
        // eyes (face -z, toward the camera)
        for (int s = -1; s <= 1; s += 2)
        {
            Prim(PrimitiveType.Sphere, heroVisual, new Vector3(s * 0.32f, 0.18f, -0.52f), new Vector3(0.34f, 0.40f, 0.30f), eyeMat);
            Prim(PrimitiveType.Sphere, heroVisual, new Vector3(s * 0.34f, 0.16f, -0.66f), new Vector3(0.16f, 0.20f, 0.14f), pupilMat);
        }
        // little feet
        for (int s = -1; s <= 1; s += 2)
            Prim(PrimitiveType.Sphere, heroVisual, new Vector3(s * 0.42f, -0.62f, -0.1f), new Vector3(0.42f, 0.30f, 0.5f), heroMat);
    }

    void BuildFallLine()
    {
        var go = new GameObject("FallLine");
        fallLine = go.transform;
        Prim(PrimitiveType.Cube, fallLine, Vector3.zero, new Vector3(WALL * 2f + 1.6f, 0.5f, 5f), fallMat);
        // jagged crest cubes so it reads as a danger band rather than a plain slab
        var crestMat = Mat(new Color(1f, 0.5f, 0.25f), 0f, 0.4f, true);
        for (int i = -7; i <= 7; i++)
            Prim(PrimitiveType.Cube, fallLine, new Vector3(i * 1.0f, 0.45f, 0f), new Vector3(0.7f, 0.7f, 4.6f), crestMat)
                .transform.localRotation = Quaternion.Euler(0, 0, 45f);
    }

    // ===================================================================== platform pool
    void BuildPlatformPool()
    {
        for (int i = 0; i < POOL; i++)
        {
            var root = new GameObject("Plat" + i).transform;
            var deck = Prim(PrimitiveType.Cube, root, Vector3.zero, new Vector3(4f, 0.5f, 2.4f), platMatA).transform;
            var spring = Prim(PrimitiveType.Cube, root, new Vector3(0, 0.45f, 0), new Vector3(0.9f, 0.4f, 0.9f), springMat).transform;
            // coin = thin emissive cylinder spun like a token
            var coin = Prim(PrimitiveType.Cylinder, root, new Vector3(0, 1.7f, 0), new Vector3(0.6f, 0.08f, 0.6f), coinMat).transform;
            coin.localRotation = Quaternion.Euler(90f, 0, 0);
            var p = new Plat { t = root, deck = deck, spring = spring, coin = coin };
            spring.gameObject.SetActive(false);
            coin.gameObject.SetActive(false);
            plats.Add(p);
        }
    }

    // ===================================================================== run lifecycle
    void ResetRun()
    {
        state = State.Playing;
        px = 0f; py = 0f; vx = 0f; vy = JUMP_BASE * 0.6f;
        startY = 0f; maxPy = 0f; prevFeet = py - P_RAD;
        score = 0; coins = 0; combo = 0; comboTimer = 0f; comboFlash = 0f;
        lastLandY = -999f; squash = 1f; spin = 0f; airT = 0f;
        attract = true; started = false; runTime = 0f;
        camFollowY = 4f; camX = 0f; fovPunch = 0f;
        fallY = (camFollowY - deathBelow) - 5f; autoRise = 0f;

        // seed the tower: first platform is a wide one right under the hero, then generate upward
        genY = -1.6f; genX = 0f;
        for (int i = 0; i < plats.Count; i++)
        {
            if (i == 0) ConfigurePlat(plats[i], 0f, -1.6f, 2.7f, PType.Normal, false);
            else GenerateNext(plats[i], i);
        }
        genY = plats[plats.Count - 1].y; genX = plats[plats.Count - 1].x;

        bannerText.text = ""; comboText.text = "";
        heroT.position = new Vector3(px, py, 0);
        UpdateCamera(0.001f, true);
        RefreshHud();
        Banner("SKY HOPPER", new Color(1f, 0.95f, 0.6f), 1.4f);
    }

    // place the next platform above the generator cursor; difficulty scales with height
    void GenerateNext(Plat p, int seedIndex)
    {
        float h = Mathf.Max(0f, genY);
        float t = Mathf.Clamp01(h / 280f);                 // 0 near ground .. 1 high up

        float gap = Mathf.Lerp(ROW_MIN, ROW_MAX, t) + Random.Range(-0.15f, 0.35f);
        gap = Mathf.Clamp(gap, ROW_MIN, ROW_MAX);
        float y = genY + gap;

        float halfW = Mathf.Lerp(2.3f, 1.25f, t) + Random.Range(-0.1f, 0.2f);
        float xrange = Mathf.Lerp(2.6f, 5.4f, t);
        float nx = genX + Random.Range(-xrange, xrange);
        float lim = WALL - halfW - 0.3f;
        if (nx > lim) nx = lim - Random.Range(0f, 1.2f);
        if (nx < -lim) nx = -lim + Random.Range(0f, 1.2f);

        // type roll (no specials for the first few so the opening is gentle)
        PType type = PType.Normal;
        if (seedIndex > 3)
        {
            float r = Random.value;
            float pSpring = 0.07f;
            float pMove = Mathf.Lerp(0.05f, 0.30f, t);
            float pBreak = Mathf.Lerp(0.0f, 0.22f, t);
            if (r < pSpring) type = PType.Spring;
            else if (r < pSpring + pMove) type = PType.Moving;
            else if (r < pSpring + pMove + pBreak) type = PType.Break;
        }

        bool coin = type != PType.Break && Random.value < 0.42f;
        ConfigurePlat(p, nx, y, halfW, type, coin);

        genY = y; genX = nx;
    }

    void ConfigurePlat(Plat p, float x, float y, float halfW, PType type, bool coin)
    {
        p.x = x; p.y = y; p.halfW = halfW; p.type = type;
        p.baseX = x; p.breaking = false; p.breakT = 0f;
        p.t.localRotation = Quaternion.identity;
        p.deck.localScale = new Vector3(halfW * 2f, 0.5f, 2.4f);
        p.deck.localPosition = Vector3.zero;
        p.deck.localRotation = Quaternion.identity;

        var mr = p.deck.GetComponent<Renderer>();
        if (type == PType.Break) mr.sharedMaterial = Mat(new Color(0.78f, 0.45f, 0.35f), 0.05f, 0.3f);
        else if (type == PType.Moving) mr.sharedMaterial = platMatB;
        else mr.sharedMaterial = platMatA;

        p.spring.gameObject.SetActive(type == PType.Spring);
        if (type == PType.Moving)
        {
            p.amp = Mathf.Min(WALL - halfW - 0.4f, Random.Range(1.6f, 3.4f));
            p.mvSpeed = Random.Range(0.7f, 1.4f);
            p.phase = Random.Range(0f, 10f);
        }
        else { p.amp = 0f; }

        p.coinAlive = coin;
        p.coin.gameObject.SetActive(coin);

        p.t.position = new Vector3(x, y, 0);
    }

    // ===================================================================== HUD
    TextMesh MakeText(float size, Color c, TextAnchor anchor)
    {
        var t = new GameObject("T").AddComponent<TextMesh>();
        t.fontSize = 96; t.characterSize = size; t.color = c; t.anchor = anchor;
        t.alignment = TextAlignment.Center;
        t.transform.SetParent(camT, false);
        t.transform.localRotation = Quaternion.identity;
        return t;
    }

    void BuildHud()
    {
        hudHeight = MakeText(0.085f, Color.white, TextAnchor.UpperLeft);
        hudScore = MakeText(0.055f, new Color(1f, 0.88f, 0.4f), TextAnchor.UpperLeft);
        hudBest = MakeText(0.055f, new Color(0.75f, 0.92f, 1f), TextAnchor.UpperRight);
        comboText = MakeText(0.09f, new Color(1f, 0.7f, 0.3f), TextAnchor.MiddleCenter);
        bannerText = MakeText(0.14f, Color.white, TextAnchor.MiddleCenter);
        dbg = MakeText(0.040f, new Color(0.6f, 1f, 0.7f), TextAnchor.LowerLeft);
        dbg.gameObject.SetActive(false);
        comboText.text = ""; bannerText.text = "";
        AdjustHud();
    }

    void AdjustHud()
    {
        if (camComp == null) return;
        float aspect = Mathf.Max(0.3f, camComp.aspect);
        halfH = HUD_Z * Mathf.Tan(camComp.fieldOfView * 0.5f * Mathf.Deg2Rad);
        halfW = halfH * aspect;
        const float REF_HALFW = 6.0f;
        hudScale = Mathf.Clamp(halfW / REF_HALFW, 0.18f, 1.3f);
        float ix = halfW * 0.90f, iy = halfH * 0.90f;

        hudHeight.transform.localPosition = new Vector3(-ix, iy, HUD_Z); hudHeight.characterSize = 0.085f * hudScale;
        hudScore.transform.localPosition = new Vector3(-ix, iy - 0.72f * hudScale, HUD_Z); hudScore.characterSize = 0.055f * hudScale;
        hudBest.transform.localPosition = new Vector3(ix, iy, HUD_Z); hudBest.characterSize = 0.055f * hudScale;
        dbg.transform.localPosition = new Vector3(-ix, -iy * 0.5f, HUD_Z); dbg.characterSize = 0.040f * hudScale;
        comboText.transform.localPosition = new Vector3(0, halfH * 0.45f, HUD_Z);
        if (comboFlash <= 0f) comboText.characterSize = 0.09f * hudScale;
    }

    void RefreshHud()
    {
        int hm = Mathf.FloorToInt(Mathf.Max(0f, maxPy - startY) / METER);
        if (hudHeight) hudHeight.text = hm + " m";
        if (hudScore) hudScore.text = "SCORE " + score + "   x" + coins;
        if (hudBest) hudBest.text = "BEST " + best + (bestCombo > 1 ? "\nCOMBO x" + bestCombo : "");
    }

    // ===================================================================== input
    float GatherSteer()
    {
        float key = Input.GetAxisRaw("Horizontal");
        float pointer = 0f; bool pressed = false; float pxs = 0f;
        if (Input.touchCount > 0) { pressed = true; pxs = Input.GetTouch(0).position.x; }
        else if (Input.GetMouseButton(0)) { pressed = true; pxs = Input.mousePosition.x; }
        if (pressed)
        {
            float n = (pxs / Mathf.Max(1f, Screen.width)) * 2f - 1f;
            pointer = Mathf.Clamp(n * 1.7f, -1f, 1f);
        }
        float raw = Mathf.Abs(key) > 0.01f ? key : pointer;

        if (Mathf.Abs(raw) > 0.01f || Input.anyKeyDown || Input.GetMouseButtonDown(0)) { attract = false; started = true; }
        if (attract) raw = AutoSteer();
        return Mathf.Clamp(raw, -1f, 1f);
    }

    // demo brain: velocity-controller toward the next platform above the hero (stable, rarely misses)
    float AutoSteer()
    {
        float bestY = float.MaxValue; float targetX = px; bool found = false;
        for (int i = 0; i < plats.Count; i++)
        {
            var p = plats[i];
            if (p.breaking) continue;
            if (p.y > py + 0.15f && p.y < bestY) { bestY = p.y; targetX = CurX(p); found = true; }
        }
        if (!found) return 0f;
        float dx = targetX - px;
        float desiredVx = Mathf.Clamp(dx * 2.4f, -MAX_VX * 0.85f, MAX_VX * 0.85f);
        return Mathf.Clamp((desiredVx - vx) * 0.4f, -1f, 1f);
    }

    // ===================================================================== main loop
    void Update()
    {
        float dt = Time.deltaTime;
        if (dt > 0.05f) dt = 0.05f;
        runTime += dt;

        if (Input.GetKeyDown(KeyCode.F1)) { showDbg = !showDbg; dbg.gameObject.SetActive(showDbg); }

        if (state == State.Dead)
        {
            TickDead(dt);
            return;
        }

        float steer = GatherSteer();

        // ---- horizontal: accelerate toward steer, friction otherwise ----
        if (Mathf.Abs(steer) > 0.01f)
        {
            vx += steer * ACCEL_X * dt;
            vx = Mathf.Clamp(vx, -MAX_VX, MAX_VX);
            facing = steer > 0 ? 1 : -1;
        }
        else vx = Mathf.MoveTowards(vx, 0f, FRICT_X * dt);

        // ---- gravity / integrate ----
        vy -= G * dt;
        px += vx * dt;
        py += vy * dt;

        // ---- wall bounce ----
        float lim = WALL - P_RAD * 0.6f;
        if (px > lim) { px = lim; vx = -Mathf.Abs(vx) * WALL_BOUNCE - 1.5f; WallFx(1); }
        else if (px < -lim) { px = -lim; vx = Mathf.Abs(vx) * WALL_BOUNCE + 1.5f; WallFx(-1); }

        // ---- move moving-platforms, spin coins ----
        AnimatePlatforms(dt);

        // ---- landing test (only while falling) ----
        float feet = py - P_RAD;
        if (vy < 0f) TryLand(feet);
        prevFeet = feet;

        // ---- collect coins ----
        CollectCoins();

        // ---- recycle platforms that dropped below the kill line ----
        RecyclePlatforms();

        // ---- height score ----
        if (py > maxPy)
        {
            maxPy = py;
            int hm = Mathf.FloorToInt((maxPy - startY) / METER);
            if (hm > score) { score = hm; if (score > best) { best = score; } }
        }

        // ---- combo decay ----
        if (comboTimer > 0f)
        {
            comboTimer -= dt;
            if (comboTimer <= 0f) EndCombo();
        }
        airT += dt;

        // ---- fall line creeps up (after the player starts) ----
        if (started)
        {
            float h = Mathf.Max(0f, maxPy);
            autoRise = Mathf.Lerp(1.6f, 6.5f, Mathf.Clamp01(h / 300f));
            fallY += autoRise * dt;
        }
        float floor = camFollowY - deathBelow;     // lowest (off-screen) rest position = generous recovery room
        if (fallY < floor) fallY = floor;
        if (fallY > camFollowY - 2.5f) fallY = camFollowY - 2.5f;  // caught up: line sits just below the hero, on screen

        // ---- death: fell below the kill line (the demo never dies; it gets a trampoline save) ----
        if (py < fallY + P_RAD)
        {
            if (attract) { py = fallY + P_RAD + 0.2f; vy = JUMP_BASE; }
            else Die();
        }

        // ---- cosmetics ----
        squash = Mathf.MoveTowards(squash, 1f, dt * 4.5f);
        spin = Mathf.Lerp(spin, vx * 2.2f, 1f - Mathf.Exp(-8f * dt));
        SyncHero();
        UpdateCamera(dt, false);
        UpdateFallLine();
        TickHud(dt);
        if (showDbg) UpdateDbg(steer, floor);
    }

    float CurX(Plat p)
    {
        if (p.type == PType.Moving) return Mathf.Clamp(p.baseX + Mathf.Sin(runTime * p.mvSpeed + p.phase) * p.amp, -(WALL - p.halfW - 0.2f), WALL - p.halfW - 0.2f);
        return p.x;
    }

    void AnimatePlatforms(float dt)
    {
        for (int i = 0; i < plats.Count; i++)
        {
            var p = plats[i];
            float cx = CurX(p);
            p.x = cx;
            if (p.breaking)
            {
                p.breakT += dt;
                // tilt + sink away after it's been used (it's already logically dead)
                p.deck.localRotation = Quaternion.Euler(0, 0, p.breakT * 60f);
                p.t.position = new Vector3(cx, p.y - p.breakT * p.breakT * 9f, 0);
            }
            else
            {
                p.t.position = new Vector3(cx, p.y, 0);
            }
            if (p.coinAlive && p.coin != null)
                p.coin.Rotate(0f, 200f * dt, 0f, Space.Self);
        }
    }

    void TryLand(float feet)
    {
        for (int i = 0; i < plats.Count; i++)
        {
            var p = plats[i];
            if (p.breaking) continue;
            float top = p.y + 0.28f;                 // deck top
            // swept vertical crossing of this platform's top, within x-extent
            if (prevFeet >= top - 0.02f && feet <= top + 0.05f)
            {
                if (Mathf.Abs(px - p.x) <= p.halfW + P_RAD * 0.5f)
                {
                    Land(p, top);
                    return;
                }
            }
        }
    }

    void Land(Plat p, float top)
    {
        py = top + P_RAD;
        prevFeet = py - P_RAD;

        float launch = (p.type == PType.Spring) ? SPRING_VY : JUMP_BASE + Mathf.Abs(vx) * JUMP_MOM;
        vy = launch;

        // squash on impact (it stretches back as it rises)
        squash = 0.6f;
        airT = 0f;
        Juice.Pop(new Vector3(px, top + 0.1f, 0f), new Color(1f, 1f, 1f, 0.8f), p.type == PType.Spring ? 12 : 5);

        // ---- combo: reward climbing past platforms quickly ----
        float climbed = p.y - lastLandY;
        if (lastLandY > -900f && climbed > 0.2f)
        {
            int rows = Mathf.Max(1, Mathf.RoundToInt(climbed / ((ROW_MIN + ROW_MAX) * 0.5f)));
            if (rows >= 2 || comboTimer > 0f)
            {
                combo += rows;
                comboTimer = 2.0f;
                comboFlash = 1f;
                int gain = rows * 5 * Mathf.Max(1, combo / 3 + 1);
                score += gain;
                if (combo > bestCombo) { bestCombo = combo; PlayerPrefs.SetInt("skyhopper_bestcombo", bestCombo); }
                comboText.text = "COMBO x" + combo + "  +" + gain;
                comboText.color = combo > 12 ? new Color(1f, 0.35f, 0.5f) : combo > 6 ? new Color(1f, 0.6f, 0.25f) : new Color(1f, 0.8f, 0.35f);
                Juice.Blip(620f + Mathf.Min(combo, 24) * 22f, 0.06f, 0.4f);
            }
        }
        lastLandY = p.y;

        if (p.type == PType.Spring)
        {
            Juice.Blip(330f, 0.12f, 0.5f); Juice.Blip(880f, 0.1f, 0.4f);
            Juice.Shake(0.28f); fovPunch = Mathf.Max(fovPunch, 7f);
            Banner("BOING!", new Color(1f, 0.85f, 0.3f), 0.7f);
        }
        else
        {
            Juice.Blip(Mathf.Lerp(300f, 520f, Mathf.Clamp01(Mathf.Abs(vx) / MAX_VX)), 0.05f, 0.3f);
            if (Mathf.Abs(vx) > MAX_VX * 0.7f) Juice.Shake(0.08f);
        }

        if (p.type == PType.Break) { p.breaking = true; p.breakT = 0f; }

        if (best > PlayerPrefs.GetInt("skyhopper_best", 0)) { PlayerPrefs.SetInt("skyhopper_best", best); PlayerPrefs.Save(); }
        RefreshHud();
    }

    void EndCombo()
    {
        if (combo >= 3)
        {
            Juice.Score(new Vector3(px, py + 1.2f, 0));
            FloatText("COMBO x" + combo + "!", new Color(1f, 0.8f, 0.35f));
        }
        combo = 0; comboTimer = 0f; comboText.text = "";
    }

    void CollectCoins()
    {
        for (int i = 0; i < plats.Count; i++)
        {
            var p = plats[i];
            if (!p.coinAlive) continue;
            float cy = p.y + 1.7f;
            float dx = px - p.x, dy = py - cy;
            if (dx * dx + dy * dy < 1.25f * 1.25f)
            {
                p.coinAlive = false;
                p.coin.gameObject.SetActive(false);
                coins++; score += 15;
                if (score > best) best = score;
                Juice.Score(new Vector3(p.x, cy, 0f));
                comboFlash = Mathf.Max(comboFlash, 0.5f);
                RefreshHud();
            }
        }
    }

    void RecyclePlatforms()
    {
        float killY = camFollowY - deathBelow - 5f;
        for (int i = 0; i < plats.Count; i++)
        {
            var p = plats[i];
            if (p.y < killY && !(p.breaking && p.breakT < 0.4f))
            {
                GenerateNext(p, 10);   // seedIndex high -> full type variety
            }
        }
    }

    void WallFx(int side)
    {
        if (Mathf.Abs(vx) < 3f && !attract) return;
        Juice.Pop(new Vector3(side * WALL, py, 0f), new Color(0.8f, 0.92f, 1f, 0.9f), 5);
        Juice.Blip(420f, 0.05f, 0.25f);
        squash = Mathf.Min(squash, 0.78f);
    }

    void SyncHero()
    {
        heroT.position = new Vector3(px, py, 0f);
        // jelly squash/stretch: volume-preserving-ish from `squash`
        float sy = squash;
        float sx = 1f / Mathf.Sqrt(Mathf.Max(0.2f, squash));
        heroVisual.localScale = new Vector3(sx, sy, sx);
        heroVisual.localRotation = Quaternion.Euler(0f, facing > 0 ? 12f : -12f, -spin);
    }

    // ===================================================================== camera / fall line
    void UpdateCamera(float dt, bool snap)
    {
        if (camT == null) return;
        camFollowY = Mathf.Max(camFollowY, py);        // only ever rises

        fovPunch = Mathf.Lerp(fovPunch, 0f, 6f * dt);
        float baseFov = 60f + Mathf.Clamp01(Mathf.Abs(vy) / 30f) * 5f;
        float fov = Mathf.Clamp(baseFov + fovPunch, 55f, 80f);
        camComp.fieldOfView = fov;

        // adaptive distance: pull back just enough that the whole shaft width is visible on any
        // aspect (so nothing — platforms, walls, bounces — ever falls off the sides on a phone),
        // but never closer than BASE_DIST so wide screens stay close & juicy.
        float aspect = Mathf.Max(0.3f, camComp.aspect);
        float halfVtan = Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad);
        float distForWidth = TARGET_HALF_W / Mathf.Max(0.05f, halfVtan * aspect);
        float dist = Mathf.Clamp(Mathf.Max(BASE_DIST, distForWidth), BASE_DIST, 30f);
        float visHalf = dist * halfVtan;               // visible world half-height at the shaft plane
        deathBelow = Mathf.Clamp(visHalf * 0.82f, 6f, 18f);

        camX = snap ? px * 0.22f : Mathf.Lerp(camX, px * 0.22f, 1f - Mathf.Exp(-5f * dt));
        float camY = camFollowY + visHalf * 0.18f;
        Vector3 want = new Vector3(camX, camY, -dist);
        camT.position = snap ? want : Vector3.Lerp(camT.position, want, 1f - Mathf.Exp(-9f * dt));
        Vector3 lookAt = new Vector3(camX * 0.5f, camFollowY + visHalf * 0.5f, 0f); // hero sits in the lower quarter
        Quaternion q = Quaternion.LookRotation(lookAt - camT.position, Vector3.up);
        camT.rotation = snap ? q : Quaternion.Slerp(camT.rotation, q, 1f - Mathf.Exp(-10f * dt));

        AdjustHud();
    }

    void UpdateFallLine()
    {
        if (fallLine == null) return;
        fallLine.position = new Vector3(0f, fallY, 0f);
        float pulse = 0.9f + Mathf.Sin(runTime * 6f) * 0.1f;
        fallLine.localScale = new Vector3(1f, pulse, 1f);
    }

    // ===================================================================== death / restart
    void Die()
    {
        if (state == State.Dead) return;
        state = State.Dead;
        EndCombo();
        if (best > PlayerPrefs.GetInt("skyhopper_best", 0)) { PlayerPrefs.SetInt("skyhopper_best", best); }
        PlayerPrefs.SetInt("skyhopper_best", Mathf.Max(best, PlayerPrefs.GetInt("skyhopper_best", 0)));
        PlayerPrefs.Save();
        Juice.Lose();
        Juice.Pop(new Vector3(px, py, 0f), new Color(1f, 0.5f, 0.4f), 16);
        int hm = Mathf.FloorToInt(Mathf.Max(0f, maxPy - startY) / METER);
        Banner("GAME OVER\n" + hm + " m   SCORE " + score + "\nTAP / R to retry", Color.white, 999f);
        comboText.text = "";
        RefreshHud();
    }

    void TickDead(float dt)
    {
        // hero keeps falling for a beat (visual)
        vy -= G * dt;
        py += vy * dt;
        heroT.position = new Vector3(px, py, 0f);
        heroVisual.Rotate(0, 0, 240f * dt, Space.Self);
        UpdateFallLine();
        AdjustHud();
        if (Input.GetKeyDown(KeyCode.R) || Input.GetKeyDown(KeyCode.Space) || Input.GetMouseButtonDown(0) || Input.touchCount > 0)
            ResetRun();
    }

    // ===================================================================== hud tick / banners
    void TickHud(float dt)
    {
        if (comboFlash > 0f)
        {
            comboFlash -= dt * 2.2f;
            comboText.characterSize = 0.09f * hudScale * (1f + Mathf.Max(0f, comboFlash) * 0.4f);
        }
        if (bannerTimer > 0f)
        {
            bannerTimer -= dt;
            if (bannerTimer <= 0f) { bannerText.text = ""; bannerText.color = Color.white; }
        }
    }

    float bannerTimer;
    void Banner(string s, Color c, float dur)
    {
        bannerText.transform.localPosition = new Vector3(0f, halfH * 0.12f, HUD_Z);
        bannerText.characterSize = 0.13f * hudScale;
        bannerText.text = s; bannerText.color = c; bannerTimer = dur;
    }

    void FloatText(string s, Color c)
    {
        bannerText.transform.localPosition = new Vector3(0f, -halfH * 0.35f, HUD_Z);
        bannerText.characterSize = 0.11f * hudScale;
        bannerText.text = s; bannerText.color = c; bannerTimer = 1.0f;
    }

    void UpdateDbg(float steer, float floor)
    {
        dbg.text = string.Format(
            "px {0:0.0} py {1:0.0}  vx {2:0.0} vy {3:0.0}\nsteer {4:0.00} squash {5:0.00}\nmaxPy {6:0.0} fallY {7:0.0} floor {8:0.0}\nscore {9} coins {10} combo {11}\nrise {12:0.0} fps {13:0}",
            px, py, vx, vy, steer, squash, maxPy, fallY, floor,
            score, coins, combo, autoRise, 1f / Mathf.Max(0.0001f, Time.smoothDeltaTime));
    }
}
