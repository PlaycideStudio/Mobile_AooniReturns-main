using DG.Tweening;
using DG.Tweening.Core;
using DG.Tweening.Plugins.Options;
using UnityEngine.Tilemaps;
using Fusion;
using System;
using System.Linq;
using UnityEngine;

public enum CharacterType
{
    Human,
    Oni
}

public class CharacterCtrl : NetworkBehaviour
{
    //==Edit: Added=====================
    private Vector2 targetPos;
    public float gridSize = 1f;

    public bool char_inMove;

    private Vector2 lastInputDir_forObstacle = Vector2.zero;
    private Vector2 lastInputDir = Vector2.zero;
    public float moveTimer; //�� �� RPG MAKER ������ �������� ���� Ÿ�̸�

    //==================================

    [SerializeField] Vector2 mapMinBounds;
    [SerializeField] Vector2 mapMaxBounds;

    [Networked] float speed { get; set; }

    [Networked, OnChangedRender(nameof(OnChangeDead))] public bool Dead { get; set; } = false;
    [Networked, OnChangedRender(nameof(OnChangeState))] public CharacterType CurrState { get; private set; } = CharacterType.Human;
    [Networked, OnChangedRender(nameof(OnChangeDir))] public Vector2 CurrDir { get; private set; } = new Vector2(0, -1);
    [Networked, OnChangedRender(nameof(OnChangeWalk))] public bool IsWalk { get; private set; } = false;

    private Rigidbody2D rb2d;
    private CircleCollider2D characterCollider;

    private OniCtrl oniCtrl;
    private HumanCtrl humanCtrl;

    private JoystickPanel joystick;
    private Animator currAnimator;

    private TweenerCore<float, float, FloatOptions> speedTween;
    private float speedTarget;
    public ArrowCtrl arrow;

    public float Speed
    {
        get => speed;
        set
        {
            if (value == 0f)
            {
                speedTween?.Kill();
                speed = 0f;
                speedTarget = 0f;
                return;
            }

            if (speedTarget == value)
            {
                return;
            }

            speedTween?.Kill();
            speedTarget = value;
            speedTween = DOTween.To(() => speed, v =>
            {
                speed = v;
            },
            value, 0.5f).SetEase(Ease.OutCubic);
        }
    }

    private void Awake()
    {
        rb2d = GetComponent<Rigidbody2D>();
        characterCollider = GetComponent<CircleCollider2D>();

        oniCtrl = GetComponentInChildren<OniCtrl>(true);
        humanCtrl = GetComponentInChildren<HumanCtrl>(true);

        currAnimator = humanCtrl.GetComponent<Animator>();

        //====ADDED: �߰��� �ڵ�(ĳ���� Ÿ�� �׸��忡 ���� �̵�)=====================================================================
        GameObject main_cam = GameObject.FindGameObjectWithTag("MainCamera");
        main_cam.transform.parent = this.gameObject.transform;
        targetPos = transform.position;
        //=======================================================================================================================
    }

    public override void Spawned()
    {
        Debug.LogError(Object.Id);
        App.Manager.Player.SubmitPlayer(this);

        if (!HasStateAuthority)
        {
            return;
        }

        Object.RequestStateAuthority();

        joystick = App.Manager.UI.GetPanel<JoystickPanel>();
    }

    public void SetCharacterState(int _index)
    {
        if (!HasStateAuthority)
        {
            return;
        }

        SetCharacterDead(false);
        CurrState = (CharacterType)_index;
    }

    private void OnChangeState()
    {
        switch (CurrState)
        {
            case CharacterType.Human:
                humanCtrl.gameObject.SetActive(true);

                currAnimator = humanCtrl.GetComponent<Animator>();
                SetupAnimator();

                oniCtrl.gameObject.SetActive(false);
                break;

            case CharacterType.Oni:
                oniCtrl.gameObject.SetActive(true);
                oniCtrl.Setup();

                currAnimator = oniCtrl.GetComponent<Animator>();
                SetupAnimator();

                humanCtrl.gameObject.SetActive(false);
                break;
        }
    }

    private void SetupAnimator()
    {
        currAnimator.SetFloat("MoveX", CurrDir.x);
        currAnimator.SetFloat("MoveY", CurrDir.y);
        currAnimator.SetBool("isWalk", IsWalk);
    }

    public void SetCharacterDead(bool _isDead)
    {
        Dead = _isDead;
    }

    private void OnChangeDead()
    {
        characterCollider.enabled = !Dead;

        if (Dead)
        {
            oniCtrl.gameObject.SetActive(false);
            humanCtrl.gameObject.SetActive(false);
        }
    }

    #region Calculate Position
    public override void FixedUpdateNetwork()
    {
        if (HasStateAuthority)
        {
            CalculatePosition();
        }
    }

    private void CalculatePosition()
    {
        var xDir = joystick.Horizontal;
        var yDir = joystick.Vertical;

        if (Mathf.Abs(xDir) > Mathf.Abs(yDir))
        {
            yDir = 0; // �¿�
        }
        else
        {
            xDir = 0; // ����
        }

        var dir = new Vector2(xDir, yDir);
        lastInputDir_forObstacle = dir.normalized;

        int layerMask = 1 << LayerMask.NameToLayer("Player");
        layerMask = ~layerMask;

        RaycastHit2D hit = Physics2D.Raycast(transform.position, lastInputDir_forObstacle, 0.2f, layerMask);

        if (hit.collider != null)
        {
            Debug.Log(hit.collider.name);
            if (hit.collider.CompareTag("Obstacle"))
            {
                Vector3 originaltPos = new Vector3(
              Mathf.Floor(transform.position.x),
              Mathf.Floor(transform.position.y),
              transform.position.z);

                transform.position = originaltPos; // Keep the current position
                char_inMove = false; // Stop movement flag
                IsWalk = false; // Stop walking animation
                                //rb2d.velocity = 6 * CurrDir;
                return;
            }
            
        }

        //====ADDED: ���ܻ��� - ���̽�ƽ�� ������ �巡���ϰ� ������ ĳ���Ͱ� �̵����̶�� �̵� ������� ���=============================
        if (Mathf.Abs(xDir) < 0.15f && Mathf.Abs(yDir) < 0.15f)
        {
            if (char_inMove)
            {
                MoveTowardsGrid();
                StoppingAnim();
            }
            return;
        }
        //==================================================================================================================

        lastInputDir = dir.normalized;

        IsWalk = lastInputDir != Vector2.zero;

        if (lastInputDir == Vector2.zero)
        {
            moveTimer = 1;
            speed = 0f;
            return;
        }

        CurrDir = lastInputDir.normalized;

        MoveTowardsGrid();

        //====DEPRECATED: ������ �ڵ�==============================================================================

        //CurrDir = dir.normalized;
        //rb2d.velocity = 24 * CurrDir;
    }

    #region ADDED: �׸��� ������ ĳ���� �̵�
    void MoveTowardsGrid()
    {
        moveTimer += 0.25f;

        if (moveTimer > 1)
        {
            //ĳ������ ���� ��ġ�� transform.position�� gridSize�� ������ => ���� �ݿø�(Mathf.Round)�Ͽ� ���� ��ġ�� �׸��� ������ ����
            Vector3 currentPos = new Vector3(
              Mathf.Round(transform.position.x / gridSize) * gridSize,
              Mathf.Round(transform.position.y / gridSize) * gridSize,
              transform.position.z);

            
            targetPos = currentPos;

            if (lastInputDir.x > 0) // �̵� - ������
                targetPos.x += gridSize;
            else if (lastInputDir.x < 0) // �̵� - ����
                targetPos.x -= gridSize;

            if (lastInputDir.y > 0) // �̵� - ��
                targetPos.y += gridSize;
            else if (lastInputDir.y < 0) // �̵� - �Ʒ�
                targetPos.y -= gridSize;


            moveTimer = 0f;
            char_inMove = true;
        }

        CurrDir = lastInputDir.normalized;

       

        transform.position = Vector3.MoveTowards(transform.position, targetPos, 0.25f);

        // ���� ĳ���Ͱ� �׸����� �߾ӿ� ���� ���������� �׸��� ������ ����
        if (Vector3.Distance(transform.position, targetPos) < 0.1f)
        {
            transform.position = new Vector3(
                     Mathf.Round(transform.position.x / gridSize) * gridSize,
                     Mathf.Round(transform.position.y / gridSize) * gridSize,
                     transform.position.z);

        }
    }

    void StoppingAnim() //������ �̵��ϴ� ĳ���Ͱ� �׸��� �߾ӿ� �����ϸ� ������ ���� ó��
    {
        if (char_inMove && Vector3.Distance(transform.position, targetPos) < 0.1f)
        {
            char_inMove = false;
            IsWalk = false;
        }
    }
    #endregion


    #endregion

    private void OnChangeDir()
    {
        currAnimator.SetFloat("MoveX", CurrDir.x);
        currAnimator.SetFloat("MoveY", CurrDir.y);
    }

    private void OnChangeWalk()
    {
        currAnimator.SetBool("isWalk", IsWalk);
    }

    public void MoveToRandomPosition(Vector2 _randomPosition)
    {
        transform.position = _randomPosition;
    }

    private Vector2 GetRandomPosition()
    {
        float randomX = UnityEngine.Random.Range(mapMinBounds.x, mapMaxBounds.x);
        float randomY = UnityEngine.Random.Range(mapMinBounds.y, mapMaxBounds.y);
        return new Vector2(randomX, randomY);
    }

    private bool IsPositionColliding(Vector2 position)
    {
        Collider2D hitCollider = Physics2D.OverlapCircle(position, 0.5f);
        return hitCollider != null; 
    }
}