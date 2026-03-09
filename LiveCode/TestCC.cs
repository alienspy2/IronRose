using RoseEngine;

public class TestCC : MonoBehaviour
{
    // -- Inspector --
    public float moveSpeed = 5f;
    public float mouseSensitivity = 2f;
    public float gravity = -9.81f;
    public float jumpForce = 5f;

    private CharacterController? cc = null;
    private float _verticalVelocity = 0f;

    public override void Start()
    {
        cc = GetComponent<CharacterController>();
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    public override void Update()
    {
        cc ??= GetComponent<CharacterController>();
        if (cc == null) return;

        float dt = Time.deltaTime;

        // ---- Mouse Look (Yaw only) ----
        float mouseX = Input.GetAxis("Mouse X") * mouseSensitivity;
        transform.Rotate(0f, mouseX, 0f);

        // ---- Movement ----
        float h = Input.GetAxis("Horizontal"); // A/D
        float v = Input.GetAxis("Vertical");   // W/S

        Vector3 move = transform.right * h + transform.forward * v;

        if (move.sqrMagnitude > 1f)
            move = move.normalized;

        move *= moveSpeed;

        // Jump
        if (cc.isGrounded && Input.GetKeyDown(KeyCode.Space))
            _verticalVelocity = jumpForce;

        // Gravity
        if (cc.isGrounded && _verticalVelocity < 0f)
            _verticalVelocity = -0.5f;

        _verticalVelocity += gravity * dt;
        move.y = _verticalVelocity;

        cc.Move(move * dt);
    }
}
