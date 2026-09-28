import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { SectionLabel } from './section-label'

describe('SectionLabel', () => {
  it('renders its children inside a level-2 heading', () => {
    render(<SectionLabel>Household</SectionLabel>)

    expect(screen.getByRole('heading', { name: 'Household', level: 2 })).toBeInTheDocument()
  })
})
