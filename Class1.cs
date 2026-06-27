using BepInEx;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;
using System;

[BepInPlugin("com.yasser.treeshateyou.godmod", "Ultimate Master Mod with Menu", "1.0.0")]
public class GodModPlugin : BaseUnityPlugin
{
    // Mod States
    public static bool isInvulnerable = false;
    public static bool isSuperFast = false;
    public static bool isFlying = false;
    public static bool isNoclip = false;
    public static bool isGrabbing = false;

    // Speeds and Forces
    public static float flySpeed = 12f;
    public static float jumpForce = 8f;
    public static float dragSpeed = 25f;

    // Rebindable Key Configuration
    public static Key godModeKey = Key.I;
    public static Key speedKey = Key.O;
    public static Key flightKey = Key.F;
    public static Key noclipKey = Key.M;
    public static Key jumpKey = Key.Space;
    public static Key menuKey = Key.L;
    public static Key flyUpKey = Key.G;
    public static Key flyDownKey = Key.H;
    public static Key cloneKey = Key.C;

    // Menu state fields
    private bool showMenu = false;
    private string bindingTarget = ""; 

    // Notification Pop-up System Fields
    private string popupMessage = "";
    private float popupTimer = 0f;
    private const float POPUP_DURATION = 2.5f; 

    // Smooth Grab/Clone Tracking Fields
    private GameObject grabbedObject = null;
    private Plane dragPlane;
    private Vector3 grabOffset;
    private bool originalHadGravity = true;
    private bool isCloneMode = false; // Tracks if the active item is a clone or a normal grab

    private void Awake()
    {
        var harmony = new Harmony("com.yasser.treeshateyou.godmod");
        harmony.PatchAll();
        
        Logger.LogInfo("Mod Loaded! Press L to open the Keybind Configuration Menu.");
    }

    private void TriggerNotification(string message)
    {
        popupMessage = message;
        popupTimer = POPUP_DURATION;
    }

    private void Update()
    {
        if (popupTimer > 0f)
        {
            popupTimer -= Time.deltaTime;
        }

        if (Keyboard.current == null || Mouse.current == null) return;

        // Menu Toggle
        if (Keyboard.current[menuKey].wasPressedThisFrame)
        {
            showMenu = !showMenu;
            bindingTarget = ""; 
            
            Cursor.lockState = showMenu ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = showMenu;
            Logger.LogInfo($"Keybind Menu toggled: {showMenu}");
        }

        if (!string.IsNullOrEmpty(bindingTarget))
        {
            ListenForKeyPress();
            return;
        }

        // Handle world inputs only if menu is closed
        if (!showMenu)
        {
            HandleCloneAndDragInput();
        }

        // --- Standard Toggles ---
        if (Keyboard.current[godModeKey].wasPressedThisFrame)
        {
            isInvulnerable = !isInvulnerable;
            TriggerNotification($"God Mode: {(isInvulnerable ? "ENABLED" : "DISABLED")}");
            Logger.LogInfo($"Invulnerability active: {isInvulnerable}");
        }

        if (Keyboard.current[speedKey].wasPressedThisFrame)
        {
            isSuperFast = !isSuperFast;
            TriggerNotification($"Super Speed: {(isSuperFast ? "ENABLED" : "DISABLED")}");
            Logger.LogInfo($"Super Speed active: {isSuperFast}");
        }

        if (Keyboard.current[flightKey].wasPressedThisFrame)
        {
            isFlying = !isFlying;
            TriggerNotification($"Flight Mode: {(isFlying ? "ENABLED" : "DISABLED")}");
            Logger.LogInfo($"Flight Mode active: {isFlying}");

            if (!isFlying && !isNoclip && PlayerController.instance != null && PlayerController.instance.rb != null)
            {
                PlayerController.instance.rb.useGravity = true;
                PlayerController.instance.rb.isKinematic = false;
            }
        }

        if (Keyboard.current[noclipKey].wasPressedThisFrame)
        {
            isNoclip = !isNoclip;
            TriggerNotification($"Noclip Phase: {(isNoclip ? "ENABLED" : "DISABLED")}");
            Logger.LogInfo($"Noclip Mode active: {isNoclip}");
            
            if (PlayerController.instance != null)
            {
                if (PlayerController.instance.col != null)
                {
                    PlayerController.instance.col.enabled = !isNoclip;
                }

                if (!isNoclip && PlayerController.instance.rb != null)
                {
                    PlayerController.instance.rb.isKinematic = false;
                    PlayerController.instance.rb.useGravity = !isFlying; 
                }
            }
        }

        if (Keyboard.current[jumpKey].wasPressedThisFrame && !isFlying && !isNoclip && PlayerController.instance != null)
        {
            Rigidbody rb = PlayerController.instance.rb;
            if (rb != null)
            {
                rb.linearVelocity = new Vector3(rb.linearVelocity.x, jumpForce, rb.linearVelocity.z);
            }
        }

        // Constant modifications
        if (PlayerController.instance != null)
        {
            PlayerController.instance.maxSpeed = isSuperFast ? 30f : 6f;

            if (isNoclip && PlayerController.instance.col != null && PlayerController.instance.col.enabled)
            {
                PlayerController.instance.col.enabled = false;
            }
        }
    }

    private void HandleCloneAndDragInput()
    {
        // 1. Trigger Clone Processing via 'C' Key
        if (Keyboard.current[cloneKey].wasPressedThisFrame && !isGrabbing)
        {
            TryTargetObject(true);
        }

        // 2. Trigger Standard Holding-Drag via Left Mouse Press
        if (Mouse.current.leftButton.wasPressedThisFrame && !isGrabbing)
        {
            TryTargetObject(false);
        }

        // 3. Constant Update Logic: Handle the active grab state
        if (isGrabbing && grabbedObject != null)
        {
            DragObjectAlongGroundPlane();

            if (isCloneMode)
            {
                // Clone mode: Drops on next single press
                if (Mouse.current.leftButton.wasPressedThisFrame)
                {
                    ReleaseObjectSmoothly("Placed Clone!");
                }
            }
            else
            {
                // Standard mode: Drops as soon as left click is completely unheld
                if (Mouse.current.leftButton.wasReleasedThisFrame)
                {
                    ReleaseObjectSmoothly("Dropped Object");
                }
            }
        }
        else if (isGrabbing && grabbedObject == null)
        {
            isGrabbing = false;
        }
    }

    private void TryTargetObject(bool shouldClone)
    {
        if (Camera.main == null) return;

        Ray ray = Camera.main.ScreenPointToRay(Mouse.current.position.ReadValue());
        RaycastHit hit;
        bool targetFound = false;
        int layerMask = ~(1 << 1 | 1 << 5); 

        if (Physics.Raycast(ray, out hit, 150f, layerMask, QueryTriggerInteraction.Ignore))
        {
            targetFound = true;
        }
        else if (Physics.SphereCast(ray, 0.4f, out hit, 150f, layerMask, QueryTriggerInteraction.Ignore))
        {
            targetFound = true;
        }

        if (targetFound && hit.collider != null)
        {
            GameObject targetSrc = hit.collider.gameObject;

            if (PlayerController.instance != null && (targetSrc == PlayerController.instance.gameObject || targetSrc.transform.IsChildOf(PlayerController.instance.transform)))
            {
                return;
            }

            isCloneMode = shouldClone;

            if (shouldClone)
            {
                // Create a completely new instance
                grabbedObject = Instantiate(targetSrc, targetSrc.transform.position, targetSrc.transform.rotation);
                TriggerNotification("Object Cloned & Dragging!");
            }
            else
            {
                // Grab the current physics item directly
                grabbedObject = targetSrc;
                TriggerNotification("Dragging Object");
            }

            isGrabbing = true;
            dragPlane = new Plane(Vector3.up, hit.point);
            grabOffset = grabbedObject.transform.position - hit.point;

            Rigidbody rb = grabbedObject.GetComponent<Rigidbody>();
            if (rb != null)
            {
                originalHadGravity = rb.useGravity;
                rb.useGravity = false;
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
        }
    }

    private void DragObjectAlongGroundPlane()
    {
        if (Camera.main == null || grabbedObject == null) return;

        Ray ray = Camera.main.ScreenPointToRay(Mouse.current.position.ReadValue());

        if (dragPlane.Raycast(ray, out float enterDistance))
        {
            Vector3 hitPointOnFloorPlane = ray.GetPoint(enterDistance);
            Vector3 targetPosition = hitPointOnFloorPlane + grabOffset;

            Rigidbody rb = grabbedObject.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.linearVelocity = (targetPosition - grabbedObject.transform.position) * dragSpeed;
                rb.angularVelocity = Vector3.zero;
            }
            else
            {
                grabbedObject.transform.position = Vector3.Lerp(grabbedObject.transform.position, targetPosition, Time.deltaTime * dragSpeed);
            }
        }
    }

    private void ReleaseObjectSmoothly(string alertMessage)
    {
        if (grabbedObject != null)
        {
            Rigidbody rb = grabbedObject.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.useGravity = originalHadGravity;
                rb.linearVelocity = Vector3.zero; 
                rb.angularVelocity = Vector3.zero;
            }
            grabbedObject = null;
        }
        isGrabbing = false;
        isCloneMode = false;
        TriggerNotification(alertMessage);
    }

    private void ListenForKeyPress()
    {
        foreach (Key k in Enum.GetValues(typeof(Key)))
        {
            if (k == Key.None) continue;
            if (k.ToString() == "IMESupported") continue;

            try
            {
                if (Keyboard.current[k].wasPressedThisFrame)
                {
                    AssignKey(bindingTarget, k);
                    bindingTarget = ""; 
                    break;
                }
            }
            catch { }
        }
    }

    private void AssignKey(string target, Key key)
    {
        switch (target)
        {
            case "GodMode": godModeKey = key; break;
            case "Speed": speedKey = key; break;
            case "Flight": flightKey = key; break;
            case "Noclip": noclipKey = key; break;
            case "Jump": jumpKey = key; break;
            case "Menu": menuKey = key; break;
            case "FlyUp": flyUpKey = key; break;
            case "FlyDown": flyDownKey = key; break;
            case "Clone": cloneKey = key; break;
        }
        TriggerNotification($"Bound {target} to {key}");
        Logger.LogInfo($"Rebound {target} to: {key}");
    }

    private void OnGUI()
    {
        if (popupTimer > 0f)
        {
            Rect popupRect = new Rect(Screen.width / 2 - 125, 25, 250, 45);
            GUI.Box(popupRect, $"<b>{popupMessage}</b>", new GUIStyle(GUI.skin.box) 
            { 
                alignment = TextAnchor.MiddleCenter, 
                fontSize = 13,
                fontStyle = FontStyle.Bold 
            });
        }

        if (!showMenu) return;

        Rect windowRect = new Rect(Screen.width / 2 - 175, Screen.height / 2 - 215, 350, 430);
        GUI.Box(windowRect, "<b>MOD KEYBIND CONFIGURATOR</b>", new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperCenter, fontSize = 14 });

        GUILayout.BeginArea(new Rect(windowRect.x + 20, windowRect.y + 35, windowRect.width - 40, windowRect.height - 50));
        GUILayout.Label("Click a button below, then press any physical key to change its mapping profile instantly.", new GUIStyle(GUI.skin.label) { wordWrap = true, alignment = TextAnchor.MiddleCenter });
        GUILayout.Space(10);

        DrawRebindRow("God Mode (Invulnerable)", "GodMode", godModeKey);
        DrawRebindRow("Super Speed Modifier", "Speed", speedKey);
        DrawRebindRow("Toggle Flight State", "Flight", flightKey);
        DrawRebindRow("Fly Up Ascension", "FlyUp", flyUpKey);
        DrawRebindRow("Fly Down Descend", "FlyDown", flyDownKey);
        DrawRebindRow("Noclip Geometry Phase", "Noclip", noclipKey);
        DrawRebindRow("Clone & Drag Item", "Clone", cloneKey);
        DrawRebindRow("Traditional Jump", "Jump", jumpKey);
        DrawRebindRow("Close/Open This Menu", "Menu", menuKey);

        GUILayout.Space(15);
        if (GUILayout.Button("Close Menu", GUILayout.Height(30)))
        {
            showMenu = false;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
        GUILayout.EndArea();
    }

    private void DrawRebindRow(string label, string targetName, Key currentKey)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(160));
        
        string displayLabel = (bindingTarget == targetName) ? "<Press Any Key...>" : currentKey.ToString();
        if (GUILayout.Button(displayLabel, GUILayout.Width(130)))
        {
            bindingTarget = targetName;
        }
        GUILayout.EndHorizontal();
        GUILayout.Space(2);
    }
}

[HarmonyPatch(typeof(PlayerController), "FixedUpdate")]
public class PlayerMovementModifierPatch
{
    [HarmonyPrefix]
    static bool Prefix(PlayerController __instance)
    {
        if (__instance == null || __instance.dead) return true;

        if (GodModPlugin.isFlying || GodModPlugin.isNoclip)
        {
            if (__instance.rb != null)
            {
                __instance.rb.useGravity = false;
                __instance.rb.isKinematic = GodModPlugin.isNoclip;

                if (Camera.main != null)
                {
                    Vector3 camEuler = Camera.main.transform.eulerAngles;
                    __instance.transform.rotation = Quaternion.Euler(0f, camEuler.y, 0f);
                }

                float upValue = 0f;
                if (GodModPlugin.isFlying && Keyboard.current != null)
                {
                    if (Keyboard.current[GodModPlugin.flyUpKey].isPressed) upValue = 1f;
                    if (Keyboard.current[GodModPlugin.flyDownKey].isPressed) upValue = -1f;
                }

                var moveActionField = AccessTools.Field(typeof(PlayerController), "moveAction");
                Vector2 moveInput = Vector2.zero;
                if (moveActionField != null)
                {
                    var actionInstance = (InputAction)moveActionField.GetValue(__instance);
                    if (actionInstance != null) moveInput = actionInstance.ReadValue<Vector2>();
                }

                Vector3 forwardVec = __instance.transform.forward * moveInput.y;
                Vector3 rightVec = __instance.transform.right * moveInput.x;
                Vector3 direction = (forwardVec + rightVec).normalized;
                
                direction.y = upValue;

                float speedValue = GodModPlugin.isSuperFast ? 30f : GodModPlugin.flySpeed;
                __instance.transform.position += direction * speedValue * Time.fixedDeltaTime;
                
                if (__instance.anim != null)
                {
                    __instance.anim.SetFloat("speed", moveInput.magnitude * (GodModPlugin.isSuperFast ? 5f : 1f));
                }

                return false; 
            }
        }
        return true;
    }
}

[HarmonyPatch(typeof(PlayerController), "Kill")]
public class PlayerKillPatch { [HarmonyPrefix] static bool Prefix() => !GodModPlugin.isInvulnerable; }