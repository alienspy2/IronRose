using RoseEngine;

public class TestCC : MonoBehaviour
{
    // -- Inspector --
    public float moveSpeed = 5f;
    public float mouseSensitivity = 2f;
    public float gravity = -9.81f;
    public float jumpForce = 5f;
    public int maxJumps = 2;

    private CharacterController? cc = null;
    private float _verticalVelocity = 0f;
    private int _jumpsRemaining = 0;
    private Vector3 _airVelocity = Vector3.zero;
    private Vector3 _startPosition;

    public override void Start()
    {
        cc = GetComponent<CharacterController>();
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
        _startPosition = transform.position;
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
        Vector3 move;
        if (cc.isGrounded)
        {
            float h = Input.GetAxis("Horizontal"); // A/D
            float v = Input.GetAxis("Vertical");   // W/S

            move = transform.right * h + transform.forward * v;

            if (move.sqrMagnitude > 1f)
                move = move.normalized;

            move *= moveSpeed;
            _airVelocity = move;
        }
        else
        {
            move = _airVelocity;
        }

        // Jump logic
        if (cc.isGrounded)
        {
            _jumpsRemaining = maxJumps;
        }

        if (Input.GetKeyDown(KeyCode.Space) && _jumpsRemaining > 0)
        {
            _verticalVelocity = jumpForce;
            _jumpsRemaining--;
        }

        // Gravity
        if (cc.isGrounded && _verticalVelocity < 0f)
            _verticalVelocity = -0.5f;

        _verticalVelocity += gravity * dt;
        move.y = _verticalVelocity;

        cc.Move(move * dt);

        // Respawn if fell too deep
        if (transform.position.y < _startPosition.y - 10f)
        {
            transform.position = _startPosition;
            _verticalVelocity = 0f;
            _airVelocity = Vector3.zero;
        }
    }
}
