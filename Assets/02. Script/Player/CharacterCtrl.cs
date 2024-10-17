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
    public float moveTimer; //좀 더 RPG MAKER 느낌의 움직임을 위한 타이머

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

        //====ADDED: 추가된 코드(캐릭터 타일 그리드에 맞춰 이동)=====================================================================
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
            yDir = 0; // 좌우
        }
        else
        {
            xDir = 0; // 상하
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

        //====ADDED: 예외사항 - 조이스틱을 빠르게 드래그하고 놨을때 캐릭터가 이동중이라면 이동 종료까지 대기=============================
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

        //====DEPRECATED: 기존의 코드==============================================================================

        //CurrDir = dir.normalized;
        //rb2d.velocity = 24 * CurrDir;
    }

    #region ADDED: 그리드 단위로 캐릭터 이동
    void MoveTowardsGrid()
    {
        moveTimer += 0.25f;

        if (moveTimer > 1)
        {
            //캐릭터의 현재 위치인 transform.position을 gridSize로 나누기 => 값을 반올림(Mathf.Round)하여 현재 위치를 그리드 단위로 맞춤
            Vector3 currentPos = new Vector3(
              Mathf.Round(transform.position.x / gridSize) * gridSize,
              Mathf.Round(transform.position.y / gridSize) * gridSize,
              transform.position.z);

            
            targetPos = currentPos;

            if (lastInputDir.x > 0) // 이동 - 오른쪽
                targetPos.x += gridSize;
            else if (lastInputDir.x < 0) // 이동 - 왼쪽
                targetPos.x -= gridSize;

            if (lastInputDir.y > 0) // 이동 - 위
                targetPos.y += gridSize;
            else if (lastInputDir.y < 0) // 이동 - 아래
                targetPos.y -= gridSize;


            moveTimer = 0f;
            char_inMove = true;
        }

        CurrDir = lastInputDir.normalized;

       

        transform.position = Vector3.MoveTowards(transform.position, targetPos, 0.25f);

        // 만약 캐릭터가 그리드의 중앙에 거의 근접했으면 그리드 단위로 맞춤
        if (Vector3.Distance(transform.position, targetPos) < 0.1f)
        {
            transform.position = new Vector3(
                     Mathf.Round(transform.position.x / gridSize) * gridSize,
                     Mathf.Round(transform.position.y / gridSize) * gridSize,
                     transform.position.z);

        }
    }

    void StoppingAnim() //기존에 이동하던 캐릭터가 그리드 중앙에 근접하면 강제로 멈춤 처리
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