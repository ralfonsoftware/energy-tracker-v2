import { useState } from 'react'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { PreferenceStrip } from './preference-strip'

const options = [
  { value: 'a', label: 'Alpha', code: 'A' },
  { value: 'b', label: 'Beta', code: 'B' },
  { value: 'c', label: 'Gamma', code: 'C' },
]

function Harness(props: { initial?: string; disabled?: boolean; pendingValue?: string; onChange?: (v: string) => void }) {
  const [value, setValue] = useState(props.initial ?? 'a')
  return (
    <>
      <span id="lbl">Letters</span>
      <PreferenceStrip
        options={options}
        value={value}
        ariaLabelledBy="lbl"
        disabled={props.disabled}
        pendingValue={props.pendingValue}
        onChange={(v) => {
          props.onChange?.(v)
          setValue(v)
        }}
      />
    </>
  )
}

describe('PreferenceStrip', () => {
  it('exposes radiogroup/radio semantics with accessible names and aria-checked', () => {
    render(<Harness initial="b" />)
    expect(screen.getByRole('radiogroup', { name: 'Letters' })).toBeInTheDocument()
    const radios = screen.getAllByRole('radio')
    expect(radios.map((r) => r.getAttribute('aria-label'))).toEqual(['Alpha', 'Beta', 'Gamma'])
    expect(radios.map((r) => r.getAttribute('aria-checked'))).toEqual(['false', 'true', 'false'])
  })

  it('uses a roving tabindex: checked = 0, others = -1', () => {
    render(<Harness initial="b" />)
    expect(screen.getAllByRole('radio').map((r) => r.getAttribute('tabindex'))).toEqual(['-1', '0', '-1'])
  })

  it('arrow keys move focus and select, wrapping at both ends', async () => {
    const user = userEvent.setup()
    render(<Harness />)
    screen.getByRole('radio', { name: 'Alpha' }).focus()

    await user.keyboard('{ArrowRight}')
    expect(screen.getByRole('radio', { name: 'Beta' })).toHaveFocus()
    expect(screen.getByRole('radio', { name: 'Beta' })).toBeChecked()

    await user.keyboard('{ArrowDown}')
    expect(screen.getByRole('radio', { name: 'Gamma' })).toBeChecked()

    await user.keyboard('{ArrowRight}')
    expect(screen.getByRole('radio', { name: 'Alpha' })).toBeChecked()

    await user.keyboard('{ArrowLeft}')
    expect(screen.getByRole('radio', { name: 'Gamma' })).toBeChecked()

    await user.keyboard('{ArrowUp}')
    expect(screen.getByRole('radio', { name: 'Beta' })).toBeChecked()
  })

  it('Home/End jump to the first/last option and select it', async () => {
    const user = userEvent.setup()
    render(<Harness initial="b" />)
    screen.getByRole('radio', { name: 'Beta' }).focus()

    await user.keyboard('{End}')
    expect(screen.getByRole('radio', { name: 'Gamma' })).toBeChecked()
    await user.keyboard('{Home}')
    expect(screen.getByRole('radio', { name: 'Alpha' })).toBeChecked()
  })

  it('clicking selects, and re-selecting the checked segment is a no-op', async () => {
    const user = userEvent.setup()
    const onChange = vi.fn()
    render(<Harness onChange={onChange} />)

    await user.click(screen.getByRole('radio', { name: 'Gamma' }))
    expect(onChange).toHaveBeenCalledTimes(1)
    expect(onChange).toHaveBeenCalledWith('c')

    await user.click(screen.getByRole('radio', { name: 'Gamma' }))
    expect(onChange).toHaveBeenCalledTimes(1)
  })

  it('disabled blocks clicks and keyboard changes', async () => {
    const user = userEvent.setup()
    const onChange = vi.fn()
    render(<Harness disabled onChange={onChange} />)

    await user.click(screen.getByRole('radio', { name: 'Beta' }))
    screen.getByRole('radio', { name: 'Alpha' }).focus()
    await user.keyboard('{ArrowRight}')
    expect(onChange).not.toHaveBeenCalled()
  })

  it('pendingValue renders a spinner on that segment', () => {
    render(<Harness pendingValue="b" />)
    const beta = screen.getByRole('radio', { name: 'Beta' })
    expect(beta.querySelector('[data-testid="preference-strip-spinner"]')).toBeInTheDocument()
    expect(screen.getByRole('radiogroup')).toHaveAttribute('aria-busy', 'true')
    expect(screen.getAllByTestId('preference-strip-spinner')).toHaveLength(1)
  })
})
