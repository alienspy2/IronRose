using RoseEngine;

public class Jump : MonoBehaviour
{
    public Rigidbody rb;


    public override void Update()
    {
        if (rb == null) {Debug.Log("rb is null");return;}

        if (Input.GetKeyDown(KeyCode.Space))
        {
            rb.AddForce(Vector3.up * 300);
            rb.AddTorque(Vector3.right * 10);
        }
    }
}
